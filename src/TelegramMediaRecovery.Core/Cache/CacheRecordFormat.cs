namespace TelegramMediaRecovery.Core.Cache;

/// <summary>Known native layouts of cache Store records.</summary>
public enum CacheRecordFormat
{
    /// <summary>Choose the layout that frames the longest valid prefix.</summary>
    Auto,

    /// <summary>Official lib_storage: three uint8 size elements; 32/48-byte records.</summary>
    TelegramDesktop,

    /// <summary>Some 64Gram builds: three uint64 size elements; 64/80-byte records.</summary>
    WideEntrySize,
}
