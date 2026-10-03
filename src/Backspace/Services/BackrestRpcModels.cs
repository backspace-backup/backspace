using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Backspace.Services;

/// <summary>
/// POCO types matching Backrest's Connect-Go RPC JSON wire format. We
/// keep only the slice of fields the <see cref="ResticBackrestEngine"/>
/// touches — Backrest's full proto surface is broader (multihost peers,
/// pairing tokens, sync, dashboards) but irrelevant to a single-user
/// desktop GUI. Adding fields later is mechanical: copy the json_name
/// from proto/v1/*.proto into a new property.
///
/// Property names are camelCase to match the proto's <c>json_name</c>
/// annotations. We rely on <see cref="JsonPropertyNameAttribute"/> for
/// the exact field name rather than the default naming policy so
/// reordering / renaming fields here doesn't risk silent wire drift.
///
/// Serialization rules:
///   - All optional/scalar fields default to non-null defaults
///     (<c>""</c>, <c>0</c>, <c>false</c>) so they're always present on
///     the wire — Backrest's Go decoder is tolerant either way, but
///     consistent presence makes wire dumps easier to read.
///   - Lists are initialized to empty lists, not null, for the same
///     reason.
///   - <c>oneof</c> fields (RetentionPolicy.policy) are modeled as
///     separate properties; the caller fills in exactly one.
/// </summary>
internal static class BackrestRpcModels { }

// ---- authentication.proto ----

internal sealed class LoginRequest
{
    [JsonPropertyName("username")] public string Username { get; set; } = "";
    [JsonPropertyName("password")] public string Password { get; set; } = "";
}

internal sealed class LoginResponse
{
    [JsonPropertyName("token")] public string Token { get; set; } = "";
}

// ---- config.proto: top-level Config + Repo + Plan + policies ----

internal sealed class BackrestConfig
{
    [JsonPropertyName("modno")] public int Modno { get; set; }
    [JsonPropertyName("version")] public int Version { get; set; }
    [JsonPropertyName("instance")] public string Instance { get; set; } = "";
    [JsonPropertyName("repos")] public List<BackrestRepo> Repos { get; set; } = new();
    [JsonPropertyName("plans")] public List<BackrestPlan> Plans { get; set; } = new();
    [JsonPropertyName("auth")] public BackrestAuth? Auth { get; set; }
    /// <summary>
    /// Catch-all for fields newer Backrest versions added that we don't
    /// have typed properties for (currently <c>sync.identity</c> in
    /// 1.14+, possibly others later). Without this passthrough we'd
    /// silently drop those fields on every GetConfig → mutate →
    /// SetConfig round-trip, which broke <c>SetConfig</c> for any
    /// config that originated on a newer Backrest — symptom was the
    /// freshly-registered plan never appearing in the daemon's
    /// in-memory state, then the immediate <c>/v1.Backrest/Backup</c>
    /// call dying with "plan not found".
    /// JsonExtensionData round-trips unknown JSON properties as
    /// <see cref="System.Text.Json.JsonElement"/> values verbatim.
    /// </summary>
    [JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement>? AdditionalFields { get; set; }
}

internal sealed class BackrestAuth
{
    [JsonPropertyName("disabled")] public bool Disabled { get; set; }
    [JsonPropertyName("users")] public List<BackrestAuthUser> Users { get; set; } = new();
}

internal sealed class BackrestAuthUser
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>bcrypt-hashed password. Backrest hashes via Authentication/HashPassword.</summary>
    [JsonPropertyName("passwordBcrypt")] public string PasswordBcrypt { get; set; } = "";
}

/// <summary>Request body for <c>/v1.Authentication/HashPassword</c>. The
/// daemon's protobuf shape wraps the plaintext password in a single
/// scalar — the response carries the base64-encoded bcrypt hash in
/// the same shape.</summary>
internal sealed class StringValue
{
    [JsonPropertyName("value")] public string Value { get; set; } = "";
}

/// <summary>Request body for <c>/v1.Authentication/Login</c>.</summary>
internal sealed class BackrestLoginRequest
{
    [JsonPropertyName("username")] public string Username { get; set; } = "";
    [JsonPropertyName("password")] public string Password { get; set; } = "";
}

/// <summary>Response from <c>/v1.Authentication/Login</c> — the JWT
/// the client must echo as <c>Authorization: Bearer {token}</c> on
/// every subsequent RPC.</summary>
internal sealed class BackrestLoginResponse
{
    [JsonPropertyName("token")] public string Token { get; set; } = "";
}

/// <summary>Request body for <c>/v1.Backrest/GetLogs</c> (server
/// streaming). The <c>ref</c> matches the <c>outputLogref</c> field
/// Backrest stamps on an operation after it runs — typically a
/// <c>c-&lt;guid&gt;</c> handle pointing at the captured stdout for
/// that operation.</summary>
internal sealed class GetLogsRequest
{
    [JsonPropertyName("ref")] public string Ref { get; set; } = "";
}

internal sealed class BackrestRepo
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("uri")] public string Uri { get; set; } = "";
    [JsonPropertyName("guid")] public string Guid { get; set; } = "";
    [JsonPropertyName("password")] public string Password { get; set; } = "";
    [JsonPropertyName("env")] public List<string> Env { get; set; } = new();
    [JsonPropertyName("flags")] public List<string> Flags { get; set; } = new();
    [JsonPropertyName("prunePolicy")] public BackrestPrunePolicy? PrunePolicy { get; set; }
    [JsonPropertyName("checkPolicy")] public BackrestCheckPolicy? CheckPolicy { get; set; }
    [JsonPropertyName("autoUnlock")] public bool AutoUnlock { get; set; }
    [JsonPropertyName("autoInitialize")] public bool AutoInitialize { get; set; }
    /// <summary>Round-trip passthrough for fields newer Backrest
    /// versions add (see <see cref="BackrestConfig.AdditionalFields"/>).</summary>
    [JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement>? AdditionalFields { get; set; }
}

internal sealed class BackrestPlan
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("repo")] public string Repo { get; set; } = "";
    [JsonPropertyName("paths")] public List<string> Paths { get; set; } = new();
    [JsonPropertyName("excludes")] public List<string> Excludes { get; set; } = new();
    [JsonPropertyName("iexcludes")] public List<string> IExcludes { get; set; } = new();
    [JsonPropertyName("retention")] public BackrestRetentionPolicy? Retention { get; set; }
    [JsonPropertyName("backupFlags")] public List<string> BackupFlags { get; set; } = new();
    /// <summary>protojson-snake-case alias for older Backrest versions.
    /// 1.13 emits camelCase; legacy releases used snake_case. Accepting
    /// both keeps round-trip GetConfig→SetConfig intact across the
    /// mid-version range we support.</summary>
    [JsonPropertyName("backup_flags")] public List<string>? BackupFlagsLegacy { set { if (value is not null) BackupFlags = value; } }
    [JsonPropertyName("skipIfUnchanged")] public bool SkipIfUnchanged { get; set; }
    /// <summary>Round-trip passthrough for fields newer Backrest
    /// versions add (see <see cref="BackrestConfig.AdditionalFields"/>).</summary>
    [JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement>? AdditionalFields { get; set; }
}

internal sealed class BackrestRetentionPolicy
{
    /// <summary>One of these three is set; the others stay default (zero/null).
    /// Mirrors the oneof in proto/v1/config.proto.</summary>
    [JsonPropertyName("policyKeepLastN")] public int? PolicyKeepLastN { get; set; }
    [JsonPropertyName("policyTimeBucketed")] public BackrestTimeBucketedCounts? PolicyTimeBucketed { get; set; }
    [JsonPropertyName("policyKeepAll")] public bool? PolicyKeepAll { get; set; }
}

internal sealed class BackrestTimeBucketedCounts
{
    [JsonPropertyName("hourly")] public int Hourly { get; set; }
    [JsonPropertyName("daily")] public int Daily { get; set; }
    [JsonPropertyName("weekly")] public int Weekly { get; set; }
    [JsonPropertyName("monthly")] public int Monthly { get; set; }
    [JsonPropertyName("yearly")] public int Yearly { get; set; }
    [JsonPropertyName("keepLastN")] public int KeepLastN { get; set; }
}

internal sealed class BackrestPrunePolicy
{
    [JsonPropertyName("maxUnusedBytes")] public long MaxUnusedBytes { get; set; }
    [JsonPropertyName("maxUnusedPercent")] public double MaxUnusedPercent { get; set; }
}

internal sealed class BackrestCheckPolicy
{
    [JsonPropertyName("structureOnly")] public bool? StructureOnly { get; set; }
    [JsonPropertyName("readDataSubsetPercent")] public double? ReadDataSubsetPercent { get; set; }
}

// ---- service.proto: request/response messages ----

internal sealed class BackupRequest
{
    /// <summary>Plan id. Field is named "value" in proto for curl-script
    /// backwards compatibility — keep the wire name as-is.</summary>
    [JsonPropertyName("value")] public string Value { get; set; } = "";
    [JsonPropertyName("dry_run")] public bool DryRun { get; set; }
}


internal sealed class ForgetRequest
{
    [JsonPropertyName("repo_id")] public string RepoId { get; set; } = "";
    [JsonPropertyName("plan_id")] public string PlanId { get; set; } = "";
    [JsonPropertyName("snapshot_id")] public string SnapshotId { get; set; } = "";
}

internal sealed class DoRepoTaskRequest
{
    [JsonPropertyName("repo_id")] public string RepoId { get; set; } = "";
    /// <summary>Numeric proto enum value. See <see cref="BackrestRepoTask"/>
    /// for the supported task ids.</summary>
    [JsonPropertyName("task")] public int Task { get; set; }
}

/// <summary>Subset of proto's DoRepoTaskRequest.Task enum we care about.
/// Values match the proto field numbers.</summary>
internal enum BackrestRepoTask
{
    TaskCheck = 3,
    TaskPrune = 2,
    TaskIndexSnapshots = 1,
    TaskStats = 4,
    TaskUnlock = 5,
    TaskForget = 6,
}

internal sealed class ListSnapshotsRequest
{
    [JsonPropertyName("repo_id")] public string RepoId { get; set; } = "";
    [JsonPropertyName("plan_id")] public string PlanId { get; set; } = "";
}

internal sealed class ResticSnapshotList
{
    [JsonPropertyName("snapshots")] public List<ResticSnapshotDto> Snapshots { get; set; } = new();
}

internal sealed class ResticSnapshotDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    // protojson lowerCamelCases proto field names by default; this
    // field was modelled as snake_case ("unix_time_ms") historically
    // and ended up always zero against Backrest 1.13 → snapshot sort
    // collapsed to insertion order. ListRevisions / restore picked
    // the wrong snapshot when more than one revision existed. Read
    // BOTH shapes so legacy / future Backrest mid-versions still
    // deserialise; PreferredName wins via the property setter
    // running in field-declaration order.
    [JsonPropertyName("unixTimeMs")] public long UnixTimeMs { get; set; }
    [JsonPropertyName("unix_time_ms")] public long UnixTimeMsLegacy { set => UnixTimeMs = value; }
    [JsonPropertyName("hostname")] public string Hostname { get; set; } = "";
    [JsonPropertyName("username")] public string Username { get; set; } = "";
    [JsonPropertyName("tree")] public string Tree { get; set; } = "";
    [JsonPropertyName("parent")] public string Parent { get; set; } = "";
    [JsonPropertyName("paths")] public List<string> Paths { get; set; } = new();
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = new();
}

internal sealed class RestoreSnapshotRequest
{
    [JsonPropertyName("plan_id")] public string PlanId { get; set; } = "";
    [JsonPropertyName("repo_id")] public string RepoId { get; set; } = "";
    [JsonPropertyName("snapshot_id")] public string SnapshotId { get; set; } = "";
    /// <summary>Path inside the snapshot to restore (empty = whole snapshot).</summary>
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    /// <summary>Local destination directory for the restore output.</summary>
    [JsonPropertyName("target")] public string Target { get; set; } = "";
}

// ---- service.proto: ListSnapshotFiles ----

internal sealed class ListSnapshotFilesRequest
{
    [JsonPropertyName("repo_guid")] public string RepoGuid { get; set; } = "";
    [JsonPropertyName("snapshot_id")] public string SnapshotId { get; set; } = "";
    /// <summary>Path inside the snapshot to list (empty = root).</summary>
    [JsonPropertyName("path")] public string Path { get; set; } = "";
}

internal sealed class ListSnapshotFilesResponse
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("entries")] public List<LsEntry> Entries { get; set; } = new();
}

internal sealed class LsEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>"file" or "dir". Other values (symlink, fifo, etc.) are
    /// passed through verbatim; the engine treats anything non-"dir"
    /// as a leaf.</summary>
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("mode")] public long Mode { get; set; }
    [JsonPropertyName("mtime")] public string Mtime { get; set; } = "";
    [JsonPropertyName("atime")] public string Atime { get; set; } = "";
    [JsonPropertyName("ctime")] public string Ctime { get; set; } = "";
}

internal sealed class RunCommandRequest
{
    [JsonPropertyName("repo_id")] public string RepoId { get; set; } = "";
    /// <summary>Restic command line after the <c>restic</c> executable —
    /// e.g. <c>"check --read-data-subset=5%"</c> or <c>"forget abc123"</c>.</summary>
    [JsonPropertyName("command")] public string Command { get; set; } = "";
}

/// <summary>Wraps an int64 — used as the return type of RunCommand
/// (the operation id Backrest enqueued) and Cancel. Wire shape:
/// <c>{ "value": "123" }</c> (string-encoded because JSON numbers
/// would silently truncate ≥ 2^53).</summary>
internal sealed class Int64Value
{
    [JsonPropertyName("value")] public string Value { get; set; } = "0";
}

// ---- streaming: GetOperationEvents ----

/// <summary>
/// Event Backrest pushes onto the GetOperationEvents stream when an
/// operation (backup, prune, check, restore) changes status.
///
/// Wire shape is the proto <c>OperationEvent</c> message's <c>oneof</c>
/// field — only one of <c>keepAlive</c>, <c>createdOperations</c>, or
/// <c>updatedOperations</c> is set per frame. Earlier we modelled this
/// as a single <c>{event, operation}</c> pair, which silently never
/// matched anything against Backrest 1.13 — every operation came in
/// under <c>createdOperations.operations[]</c> / <c>updatedOperations.operations[]</c>
/// instead. The orchestrator then defaulted lastStatus="" and
/// mis-classified every successful restic backup as Failed. <see cref="GetAllOperations"/>
/// is the helper our pump uses to walk both lists in one shot.
/// </summary>
internal sealed class BackrestOperationEvent
{
    [JsonPropertyName("keepAlive")] public BackrestEmptyMarker? KeepAlive { get; set; }
    [JsonPropertyName("createdOperations")] public BackrestOperationList? CreatedOperations { get; set; }
    [JsonPropertyName("updatedOperations")] public BackrestOperationList? UpdatedOperations { get; set; }
    [JsonPropertyName("deletedOperations")] public BackrestInt64List? DeletedOperations { get; set; }

    /// <summary>True when this is a keep-alive frame (nothing else set).</summary>
    public bool IsKeepAlive => KeepAlive is not null
                               && CreatedOperations is null
                               && UpdatedOperations is null
                               && DeletedOperations is null;

    /// <summary>Yield every operation in this frame (created + updated). Use
    /// this when the consumer cares about state transitions regardless of
    /// whether the op is new or an in-place update — true for our terminal-
    /// status watcher.</summary>
    public IEnumerable<BackrestOperation> GetAllOperations()
    {
        if (CreatedOperations?.Operations is { } created)
            foreach (var op in created) yield return op;
        if (UpdatedOperations?.Operations is { } updated)
            foreach (var op in updated) yield return op;
    }
}

/// <summary>Empty marker — the keep_alive payload in the OperationEvent
/// oneof. The wire shape is just <c>{}</c>; this class exists so the
/// JSON deserializer can construct an instance that proves the oneof
/// arm was selected.</summary>
internal sealed class BackrestEmptyMarker { }

internal sealed class BackrestOperationList
{
    [JsonPropertyName("operations")] public List<BackrestOperation>? Operations { get; set; }
}

internal sealed class BackrestInt64List
{
    [JsonPropertyName("values")] public List<string>? Values { get; set; }
}

internal sealed class BackrestOperation
{
    [JsonPropertyName("id")] public string Id { get; set; } = "0";
    [JsonPropertyName("planId")] public string PlanId { get; set; } = "";
    [JsonPropertyName("repoId")] public string RepoId { get; set; } = "";
    /// <summary>One of "STATUS_PENDING", "STATUS_INPROGRESS", "STATUS_SUCCESS",
    /// "STATUS_WARNING", "STATUS_ERROR", "STATUS_USER_CANCELLED" — see proto.</summary>
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("displayMessage")] public string DisplayMessage { get; set; } = "";
    [JsonPropertyName("unixTimeStartMs")] public long UnixTimeStartMs { get; set; }
    [JsonPropertyName("unixTimeEndMs")] public long UnixTimeEndMs { get; set; }
    /// <summary>Present on RunCommand-shaped operations (Backrest's
    /// oneof <c>op</c> picks one of operationBackup / operationRestore
    /// / operationRunCommand / ... per operation type). We only need
    /// to read the run-command shape for now — the dry-run preview
    /// pulls captured stdout via <see cref="OperationRunCommand.OutputLogref"/>.</summary>
    [JsonPropertyName("operationRunCommand")] public BackrestOperationRunCommand? OperationRunCommand { get; set; }
    [JsonPropertyName("operationBackup")] public System.Text.Json.JsonElement? OperationBackup { get; set; }
    [JsonPropertyName("operationForget")] public System.Text.Json.JsonElement? OperationForget { get; set; }
    [JsonPropertyName("operationPrune")] public System.Text.Json.JsonElement? OperationPrune { get; set; }
    [JsonPropertyName("operationCheck")] public System.Text.Json.JsonElement? OperationCheck { get; set; }
}

internal sealed class BackrestOperationRunCommand
{
    [JsonPropertyName("command")] public string Command { get; set; } = "";
    /// <summary>Handle to Backrest's captured stdout for this run.
    /// Pass to <c>/v1.Backrest/GetLogs</c> (streaming) to drain the
    /// content. Empty before the operation has produced any output.</summary>
    [JsonPropertyName("outputLogref")] public string OutputLogref { get; set; } = "";
    [JsonPropertyName("outputSizeBytes")] public string OutputSizeBytes { get; set; } = "0";
}

internal sealed class CheckRepoExistsRequest
{
    [JsonPropertyName("repo")] public BackrestRepo? Repo { get; set; }
}

internal sealed class CheckRepoExistsResponse
{
    [JsonPropertyName("exists")] public bool Exists { get; set; }
    [JsonPropertyName("error")] public string Error { get; set; } = "";
}

/// <summary>
/// Body of <c>/v1.Backrest/AddRepo</c>. The handler runs <c>restic init</c>
/// (or adopts an already-initialised on-disk repo), reads the restic
/// config-id, stamps it as the canonical 64-char Guid, and writes the
/// result into Backrest's config in one atomic step. Sending a SetConfig
/// with <c>Guid=""</c> + <c>AutoInitialize=true</c> would pass SetConfig's
/// per-repo validator but be rejected by Backrest's orchestrator-apply
/// step ("guid is required for all repos") — the orchestrator wants
/// a non-empty Guid even when AutoInitialize is on, and AddRepo is
/// the only RPC that actually gives us one for a brand-new repo.
/// </summary>
internal sealed class AddRepoRequest
{
    [JsonPropertyName("repo")] public BackrestRepo Repo { get; set; } = new();
}

/// <summary>
/// Selector for the GetOperations RPC. We use the repo_guid filter
/// to ask "what operations exist for THIS repo?" — combined with
/// last_n on the request and a status filter on the result, that
/// gives us a cheap "is the repo idle?" probe without subscribing
/// to the event stream.
/// </summary>
internal sealed class OpSelector
{
    [JsonPropertyName("repoGuid")] public string? RepoGuid { get; set; }
    /// <summary>Optional op-id filter — Backrest's proto names this
    /// <c>ids</c> with field number 1. When set, the response
    /// contains only operations whose id matches one of the listed
    /// values. Used by <c>WaitForOperationTerminalAsync</c> to poll
    /// a SPECIFIC op's status by id (avoids racing against the
    /// event-stream subscription which can miss a fast op's
    /// terminal event).</summary>
    [JsonPropertyName("ids")] public string[]? Ids { get; set; }
}

internal sealed class GetOperationsRequest
{
    [JsonPropertyName("selector")] public OpSelector Selector { get; set; } = new();
    [JsonPropertyName("lastN")] public long LastN { get; set; }
}

// ---- Connect-Go transport helpers ----

/// <summary>Connect protocol error envelope. Connect over JSON returns a
/// 200-ish or 4xx response with a JSON body of this shape on errors.
/// See https://connectrpc.com/docs/protocol#error-end-stream.</summary>
internal sealed class ConnectError
{
    [JsonPropertyName("code")] public string Code { get; set; } = "";
    [JsonPropertyName("message")] public string Message { get; set; } = "";
}
