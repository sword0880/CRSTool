using System.Globalization;
using CRS.ExchangeRates;
using CRS.Worker.Ipc;

namespace CRS.Worker.Service;

public sealed partial class EncryptedTaskRepository
{
    internal CentralParitySnapshot? ReadAnnualExchangeRateSnapshot(int year, string origin = "Official", string ownerSid = "")
    {
        if (year is < 2000 or > 2100 || origin is not ("Official" or "DesktopLegacy") ||
            (origin == "Official" && ownerSid.Length != 0) || (origin == "DesktopLegacy" && (string.IsNullOrWhiteSpace(ownerSid) || ownerSid.Length > 256 || ownerSid.Any(char.IsControl))))
            throw new ArgumentOutOfRangeException(nameof(year));
        return Persist(() =>
        {
            lock (gate)
            {
                using var query = Command("""
                    SELECT version,from_date,through_date,fetched_at
                    FROM exchange_rate_snapshot WHERE year=$year AND origin=$origin AND owner_sid=$owner
                    """, ("$year", year), ("$origin", origin), ("$owner", ownerSid));
                using var row = query.ExecuteReader();
                if (!row.Read()) return null;
                var version = row.GetInt32(0);
                var from = DateOnly.ParseExact(row.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture);
                var through = DateOnly.ParseExact(row.GetString(2), "yyyy-MM-dd", CultureInfo.InvariantCulture);
                var fetched = DateTimeOffset.Parse(row.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.None);
                row.Close();
                using var quotes = Command("""
                    SELECT quote_date,usd,hkd FROM exchange_rate_quote
                    WHERE year=$year AND origin=$origin AND owner_sid=$owner ORDER BY quote_date
                    """, ("$year", year), ("$origin", origin), ("$owner", ownerSid));
                using var reader = quotes.ExecuteReader();
                var items = new List<CentralParityQuote>();
                while (reader.Read())
                {
                    if (!decimal.TryParse(reader.GetString(1), NumberStyles.Number, CultureInfo.InvariantCulture, out var usd) ||
                        !decimal.TryParse(reader.GetString(2), NumberStyles.Number, CultureInfo.InvariantCulture, out var hkd))
                        return null;
                    items.Add(new(DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture), usd, hkd));
                }
                var snapshot = new CentralParitySnapshot(version, from, through, fetched, items.ToArray());
                return snapshot.IsValid(year) ? snapshot : null;
            }
        });
    }

    // 首次写入的同日报价不可被后续下载或人工修订覆盖；新日期只追加。
    internal CentralParitySnapshot SaveAnnualExchangeRateSnapshot(int year, CentralParitySnapshot snapshot, string origin = "Official", string ownerSid = "")
    {
        if (year is < 2000 or > 2100 || origin is not ("Official" or "DesktopLegacy") ||
            (origin == "Official" && ownerSid.Length != 0) || (origin == "DesktopLegacy" && string.IsNullOrWhiteSpace(ownerSid)) ||
            snapshot is null || !snapshot.IsValid(year))
            throw new InvalidDataException("年度汇率快照无效。");
        return Persist(() =>
        {
            lock (gate)
            {
                using var tx = db.BeginTransaction();
                using (var metadata = Command("""
                    SELECT version,from_date FROM exchange_rate_snapshot
                    WHERE year=$year AND origin=$origin AND owner_sid=$owner
                    """, ("$year", year), ("$origin", origin), ("$owner", ownerSid)))
                using (var row = metadata.ExecuteReader())
                {
                    if (row.Read() && (row.GetInt32(0) != snapshot.Version ||
                        row.GetString(1) != snapshot.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))
                        throw new InvalidDataException("年度汇率快照的版本或起始日期发生冲突。");
                }
                Execute("""
                    INSERT INTO exchange_rate_snapshot(year,origin,owner_sid,version,from_date,through_date,fetched_at)
                    VALUES($year,$origin,$owner,$version,$from,$through,$fetched)
                    ON CONFLICT(year,origin,owner_sid) DO UPDATE SET
                      through_date=CASE WHEN excluded.through_date>through_date THEN excluded.through_date ELSE through_date END,
                      fetched_at=CASE WHEN excluded.fetched_at>fetched_at THEN excluded.fetched_at ELSE fetched_at END
                    """, ("$year", year), ("$origin", origin), ("$owner", ownerSid), ("$version", snapshot.Version),
                    ("$from", snapshot.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                    ("$through", snapshot.Through.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                    ("$fetched", snapshot.FetchedAt.ToString("O", CultureInfo.InvariantCulture)));
                foreach (var quote in snapshot.Quotes)
                {
                    Execute("""
                        INSERT OR IGNORE INTO exchange_rate_quote(year,origin,owner_sid,quote_date,usd,hkd)
                        VALUES($year,$origin,$owner,$date,$usd,$hkd)
                        """, ("$year", year), ("$origin", origin), ("$owner", ownerSid),
                        ("$date", quote.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                        ("$usd", quote.Usd.ToString("G29", CultureInfo.InvariantCulture)),
                        ("$hkd", quote.Hkd.ToString("G29", CultureInfo.InvariantCulture)));
                }
                tx.Commit();
                return ReadAnnualExchangeRateSnapshot(year, origin, ownerSid)
                    ?? throw new InvalidDataException("年度汇率快照写入后未通过完整性校验。");
            }
        });
    }

    internal void ImportLegacyAnnualRates(IReadOnlyList<LegacyAnnualExchangeRateRecord> rates, string sourceHash)
    {
        if (rates.Count is < 1 or > 100 || !(sourceHash.Length == 64 && sourceHash.All(Uri.IsHexDigit)))
            throw new InvalidDataException("旧年度汇率导入清单无效。");
        Persist(() =>
        {
            lock (gate)
            {
                using var tx = db.BeginTransaction();
                using (var existing = Command("SELECT COUNT(*) FROM exchange_rate_migration WHERE migration_id='legacy-annual-rates-v1'"))
                    if (Convert.ToInt32(existing.ExecuteScalar(), CultureInfo.InvariantCulture) != 0) return;
                var seen = new HashSet<(int Year, string Currency)>();
                foreach (var rate in rates)
                {
                    if (rate.Year is < 2000 or > 2100 || rate.Currency.Length != 3 ||
                        rate.Currency.Any(ch => ch is < 'A' or > 'Z') || rate.Value <= 0 ||
                        (rate.Currency == "CNY" && rate.Value != 1) ||
                        !seen.Add((rate.Year, rate.Currency)))
                        throw new InvalidDataException("旧年度汇率包含无效或重复记录。");
                    Execute("""
                        INSERT INTO legacy_annual_exchange_rate(year,currency,value,source,source_url,method,
                            start_date,end_date,provisional,source_confirmed,first_observation,observation_count,usd_sum,hkd_sum)
                        VALUES($year,$currency,$value,$source,$url,$method,$start,$end,$provisional,$confirmed,
                            $first,$count,$usdSum,$hkdSum)
                        ON CONFLICT(year,currency) DO NOTHING
                        """, ("$year", rate.Year), ("$currency", rate.Currency),
                        ("$value", rate.Value.ToString("G29", CultureInfo.InvariantCulture)),
                        ("$source", rate.Source), ("$url", rate.Url), ("$method", rate.Method),
                        ("$start", rate.Start), ("$end", rate.End), ("$provisional", rate.Provisional ? 1 : 0),
                        ("$confirmed", rate.SourceConfirmed ? 1 : 0), ("$first", rate.FirstObservation),
                        ("$count", rate.ObservationCount), ("$usdSum", rate.UsdSum), ("$hkdSum", rate.HkdSum));
                }
                Execute("""
                    INSERT INTO exchange_rate_migration(migration_id,source_hash,imported_utc,imported_rows)
                    VALUES('legacy-annual-rates-v1',$hash,$utc,$count)
                    """, ("$hash", sourceHash), ("$utc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)),
                    ("$count", rates.Count));
                tx.Commit();
            }
        });
    }

    internal LegacyAnnualExchangeRateRecord[] ReadLegacyAnnualRates(int year)
    {
        if (year is < 2000 or > 2100) throw new ArgumentOutOfRangeException(nameof(year));
        return Persist(() =>
        {
            lock (gate)
            {
                using var query = Command("""
                    SELECT year,currency,value,source,source_url,method,start_date,end_date,provisional,
                        source_confirmed,first_observation,observation_count,usd_sum,hkd_sum
                    FROM legacy_annual_exchange_rate WHERE year=$year ORDER BY currency
                    """, ("$year", year));
                using var reader = query.ExecuteReader();
                var result = new List<LegacyAnnualExchangeRateRecord>();
                while (reader.Read())
                {
                    if (!decimal.TryParse(reader.GetString(2), NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
                        throw new InvalidDataException("数据库中的历史汇率数值无效。");
                    result.Add(new(reader.GetInt32(0), reader.GetString(1), value, reader.GetString(3),
                        reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
                        reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetInt32(8) == 1,
                        reader.GetInt32(9) == 1, reader.IsDBNull(10) ? null : reader.GetString(10),
                        reader.IsDBNull(11) ? null : reader.GetInt32(11),
                        reader.IsDBNull(12) ? null : reader.GetString(12),
                        reader.IsDBNull(13) ? null : reader.GetString(13)));
                }
                return result.ToArray();
            }
        });
    }
}
