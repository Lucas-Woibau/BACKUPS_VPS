namespace VpsBackupManager.Data;

// Row classes mapped by Dapper (snake_case columns -> PascalCase properties).

public sealed class UserRow
{
    public long Id { get; set; }
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public long CreatedAt { get; set; }
    public long? LastLoginAt { get; set; }
}

public sealed class ConnectionRow
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string DbType { get; set; } = "";
    public string Host { get; set; } = "";
    public long Port { get; set; }
    public string Username { get; set; } = "";
    public string PasswordEnc { get; set; } = "";
    public string SslMode { get; set; } = "disabled";
    public string Options { get; set; } = "{}";
    public bool BackupAll { get; set; } = true;
    public bool IncludeSystem { get; set; }
    public string SelectedDatabases { get; set; } = "[]";
    public string ExcludedDatabases { get; set; } = "[]";
    public string DiscoveredDatabases { get; set; } = "[]";
    public bool Enabled { get; set; } = true;
    public bool? LastTestOk { get; set; }
    public long? LastTestAt { get; set; }
    public string? LastTestMessage { get; set; }
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
}

public sealed class ScheduleRow
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string Kind { get; set; } = "weekly";
    public string DaysOfWeek { get; set; } = "[]";
    public string Times { get; set; } = "[]";
    public long? IntervalMinutes { get; set; }
    public string TargetType { get; set; } = "all";
    public long? TargetConnectionId { get; set; }
    public string? TargetDatabase { get; set; }
    public long? LastTriggeredAt { get; set; }
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
}

public sealed class RunRow
{
    public string Id { get; set; } = "";
    public string Trigger { get; set; } = "";
    public long? ScheduleId { get; set; }
    public string TargetType { get; set; } = "all";
    public long? TargetConnectionId { get; set; }
    public string? TargetDatabase { get; set; }
    public string Status { get; set; } = "";
    public long Total { get; set; }
    public long Succeeded { get; set; }
    public long Warnings { get; set; }
    public long Failed { get; set; }
    public string? Message { get; set; }
    public long StartedAt { get; set; }
    public long? FinishedAt { get; set; }
}

public sealed class BackupRow
{
    public string Id { get; set; } = "";
    public string RunId { get; set; } = "";
    public long? ConnectionId { get; set; }
    public string ConnectionName { get; set; } = "";
    public string DbType { get; set; } = "";
    public string DatabaseName { get; set; } = "";
    public string Status { get; set; } = "";
    public long? StartedAt { get; set; }
    public long? FinishedAt { get; set; }
    public long? DurationMs { get; set; }
    public long? RawSize { get; set; }
    public long? CompressedSize { get; set; }
    public long? FinalSize { get; set; }
    public string? Compression { get; set; }
    public bool Encrypted { get; set; }
    public string? RawSha256 { get; set; }
    public string? ChecksumSha256 { get; set; }
    public string? FileName { get; set; }
    public string? LocalPath { get; set; }
    public string? RemotePath { get; set; }
    public long UploadAttempts { get; set; }
    public string? Warning { get; set; }
    public string? Error { get; set; }
    public long? LocalDeletedAt { get; set; }
    public long? RemoteDeletedAt { get; set; }
    public long CreatedAt { get; set; }
}

public sealed class EventRow
{
    public long Id { get; set; }
    public string? RunId { get; set; }
    public string? BackupId { get; set; }
    public string Level { get; set; } = "info";
    public string Message { get; set; } = "";
    public long CreatedAt { get; set; }
}

public static class BackupStatus
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Success = "success";
    public const string Warning = "warning";
    public const string Error = "error";
}
