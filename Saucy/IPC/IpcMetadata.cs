using System;
namespace Saucy.IPC;

/// <summary>各整合對象插件的 InternalName（查是否已載入用）。</summary>
internal static class IPCNames
{
    public const string Lifestream = "Lifestream";
    public const string BossMod = "BossModReborn";
    public const string Vnavmesh = "vnavmesh";
    public const string Questionable = "Questionable";
    public const string AutoRetainer = "AutoRetainer";
}

[AttributeUsage(AttributeTargets.Class)]
internal sealed class IPCAttribute(string name) : Attribute
{
    /// <summary>插件的 InternalName。</summary>
    public string Name { get; } = name;

    /// <summary>EzIPC 端點前綴；不給就等於 <see cref="Name"/>。</summary>
    public string? IpcPrefix { get; set; }
}
