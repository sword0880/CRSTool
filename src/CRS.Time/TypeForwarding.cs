using System.Runtime.CompilerServices;
using CRS.Timing;

// Keep the pre-existing assembly name CRS.Time and its type identities resolvable.
// Consumers of old binary plugins still load CRS.Time.dll; implementations live in CRS.Common.dll.
[assembly: TypeForwardedTo(typeof(BeijingClock))]
[assembly: TypeForwardedTo(typeof(BeijingDateTimeOffsetConverter))]
