using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using AgentId = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentId;

namespace Saucy.Framework;

public static unsafe class AgentHelper
{
    public static bool IsActive(AgentId agentId)
    {
        var agent = AgentModule.Instance()->GetAgentByInternalId(agentId);
        return agent != null && agent->IsAgentActive();
    }

    public static bool IsAddonOwnedBy(AtkUnitBase* addon, AgentId agentId)
    {
        if (addon == null ||
            !RaptureAtkModule.Instance()->AddonCallbackMapping.TryGetValue(addon->Id, out var callbackEntry, false))
        {
            return false;
        }

        var agent = AgentModule.Instance()->GetAgentByInternalId(agentId);
        return agent == callbackEntry.AgentInterface;
    }

    /// <summary>
    /// 診斷用：查出這個 addon 的回呼登記在誰名下。
    ///
    /// 為什麼需要：<see cref="IsAddonOwnedBy"/> 只能回答「是不是某個特定 agent」，
    /// 回 false 時分不出「屬於別的 agent」與「根本不是 agent 開的」。
    /// <c>AddonCallbackEntry</c> 的偏移 0 是個 union（<c>EventInterface</c> ／ <c>AgentInterface</c>），
    /// 事件腳本開的視窗登記的就不是 agent —— 那正是「同一個判定在一處成立、在另一處不成立」的來源。
    ///
    /// 🔴 這裡刻意**不**呼叫 <c>GetAgentByInternalId</c> 去逐一試 id：那是以 id 索引的原生函式。
    /// 改成讀 <c>AgentModule</c> 自己的固定大小陣列（CS 宣告 484 格）逐格比對指標，
    /// 邊界由型別保證，不存在越界問題。取到的指標**當幀用完就丟**，不保存。
    /// </summary>
    /// <param name="agentId">命中的 agent 內部 id（也就是 <see cref="AgentId"/> 的數值）。</param>
    /// <param name="eventKind">回呼登記的事件種類，沒有登記時為 0。</param>
    /// <returns>true = 擁有者是某個 agent；false = 沒有登記，或擁有者不在 agent 陣列裡（多半是事件介面）。</returns>
    public static bool TryGetOwnerAgentId(AtkUnitBase* addon, out uint agentId, out ulong eventKind)
    {
        agentId = uint.MaxValue;
        eventKind = 0;

        var atkModule = RaptureAtkModule.Instance();
        if (addon == null || atkModule == null ||
            !atkModule->AddonCallbackMapping.TryGetValue(addon->Id, out var callbackEntry, false))
        {
            return false;
        }

        eventKind = callbackEntry.EventKind;

        var owner = callbackEntry.AgentInterface;
        var agentModule = AgentModule.Instance();
        if (owner == null || agentModule == null)
        {
            return false;
        }

        var agents = agentModule->Agents;
        for (var i = 0; i < agents.Length; i++)
        {
            if (agents[i].Value == owner)
            {
                agentId = (uint)i;
                return true;
            }
        }

        return false;
    }

    /// <summary>把擁有者寫成一行可讀的診斷字串。</summary>
    public static string DescribeOwner(AtkUnitBase* addon)
    {
        if (addon == null)
        {
            return "addon=null";
        }

        if (!TryGetOwnerAgentId(addon, out var agentId, out var eventKind))
        {
            return eventKind == 0
                ? "沒有回呼登記（不是 agent 開的）"
                : $"非 agent 擁有者（eventKind={eventKind}）";
        }

        return $"agent#{agentId}({(AgentId)agentId}) eventKind={eventKind}";
    }
}
