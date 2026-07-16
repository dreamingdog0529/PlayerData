using System;

namespace PlayerData;

// Marks a partial class as a save-session surface. Documents are declared as class-level
// [PlayerDataSingle] / [PlayerDataCollection] attributes (stack one per document); the
// generator writes the corresponding properties fully. Multiple sessions per compilation are
// allowed.
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class PlayerDataSessionAttribute : Attribute
{
    // When true, DisposeAsync commits if dirty. Default is false (dispose is a no-op).
    public bool AutoCommitOnDispose { get; set; }
}
