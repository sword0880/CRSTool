using System.Text.Json;
using CRS.Timing;

namespace CRS.Worker.Service;

public sealed partial class EncryptedTaskRepository
{
    public void QueueFailureNotice(string id,string plugin,string job,string result) => Persist(() =>
    {
        lock (gate) Execute("INSERT OR IGNORE INTO task_failure_notice(id,plugin,job,result,utc) VALUES($id,$p,$j,$r,$utc)",
            ("$id",id),("$p",plugin),("$j",job),("$r",result),("$utc",Now));
    });

    public TaskFailureNotice? NextFailureNotice() => Persist<TaskFailureNotice?>(() =>
    {
        lock (gate)
        {
            using var command=Command("SELECT id,plugin,job,result,utc,attempted FROM task_failure_notice ORDER BY utc,id LIMIT 1");
            using var row=command.ExecuteReader();
            return row.Read()?new(row.GetString(0),row.GetString(1),row.GetString(2),row.GetString(3),
                BeijingClock.Parse(row.GetString(4)),JsonSerializer.Deserialize<string[]>(row.GetString(5))!):null;
        }
    });

    public void MarkFailureChannel(TaskFailureNotice notice,string channel) => Persist(() =>
    {
        lock (gate) Execute("UPDATE task_failure_notice SET attempted=$a WHERE id=$id",
            ("$a",JsonSerializer.Serialize(notice.Attempted.Append(channel))),("$id",notice.Id));
    });

    public void CompleteFailureNotice(string id) => Persist(() =>
    {
        lock (gate) Execute("DELETE FROM task_failure_notice WHERE id=$id",("$id",id));
    });
}
