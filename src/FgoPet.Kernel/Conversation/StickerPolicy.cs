using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FgoPet.Kernel.Agent;

namespace FgoPet.App.Dialogue;

public sealed record StickerCandidate(string AssetId, string Intent, string Utility, string Salience, string Context, string Position);
public sealed record StickerSelection(string AssetId, string Intent, string Position);
public sealed record StickerAsset(string Id, string Intent, string Label, string RelativePath, string Sha256, bool External = false,
    string? Guidance = null);

public interface IStickerCatalogSource
{
    IReadOnlyList<StickerAsset> ReadAssets();
}

/// <summary>Presentation only. Model nominations never grant filesystem access or override host budgets.</summary>
public static class StickerPolicy
{
    internal const string PromptPrefix = "可选本地图文表情：";
    public static bool IsMash(string role, string package) => role == "mash_kyrielight" && package == "official.mash";
    public static bool IsWebAssetId(string? id) => id is { Length: 52 } && id.StartsWith("web.", StringComparison.Ordinal)
        && id.AsSpan(4).IndexOfAnyExcept("0123456789abcdef") < 0;
    public static bool IsWebIntent(string? intent) => intent is "playful" or "surprise" or "confused" or "curious"
        or "annoyed" or "sad" or "tired" or "laugh" or "panic" or "facepalm" or "absurd";

    public static bool HasBusinessTools(AgentRunCheckpoint checkpoint)
    {
        var calls = checkpoint.CompletedSteps.SelectMany(step => step.Calls).Concat(checkpoint.Calls).ToArray();
        return calls.Any(call => call.Call.Name != NativeFinalization.Name)
            || checkpoint.Snapshot.ToolCalls > 0 && calls.Length == 0;
    }

    public static StickerCandidate? ReadCandidate(JsonElement root)
    {
        if (!root.TryGetProperty("sticker", out var value) || value.ValueKind != JsonValueKind.Object) return null;
        var properties = value.EnumerateObject().ToArray();
        if (properties.Length != 6 || properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != 6) return null;
        string? Read(string name) => value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String
            && item.GetString() is { Length: > 0 and <= 64 } text ? text : null;
        var id = Read("assetId"); var intent = Read("intent"); var utility = Read("utility");
        var salience = Read("salience"); var context = Read("context"); var position = Read("position");
        var asset = StickerCatalog.Find(id) ?? (IsWebAssetId(id) && IsWebIntent(intent)
            ? new StickerAsset(id!, intent!, "", "", "", External: true) : null);
        return asset is not null && asset.Intent == intent && utility is "replace" or "enhance"
            && salience is "medium" or "high" && context is "social" or "playful"
            && position is "before" or "after" && (!(asset.External || asset.Intent == "annoyed") || context == "playful")
            ? new(id!, intent!, utility!, salience!, context!, position!) : null;
    }

    /// <param name="history">Latest completed reply first; null entries are complete replies without a sticker.</param>
    public static StickerSelection? Select(StickerCandidate? candidate, IReadOnlyList<StickerSelection?> history,
        string deliveryKey, string reply, bool blocked = false, IReadOnlyList<StickerAsset>? catalog = null)
    {
        if (blocked || candidate is null || reply.Length > 600 || reply.Contains("```", StringComparison.Ordinal)
            || reply.Contains("![", StringComparison.Ordinal) || reply.Contains("<img", StringComparison.OrdinalIgnoreCase)) return null;
        // Revalidate even when invoked with a candidate from protected or legacy state.
        var assets = catalog ?? StickerCatalog.Assets;
        var asset = assets.FirstOrDefault(a => a.Id == candidate.AssetId);
        if (asset is null || asset.Intent != candidate.Intent || candidate.Utility is not ("replace" or "enhance")
            || candidate.Salience is not ("high" or "medium") || candidate.Context is not ("social" or "playful")
            || candidate.Position is not ("before" or "after") || (asset.External || asset.Intent == "annoyed") && candidate.Context != "playful") return null;
        if (history.Take(3).Any(s => s is not null) || history.Take(5).Any(s => s?.Intent == candidate.Intent)
            || history.Take(9).Count(s => s is not null) >= 2) return null;
        if (IsWebAssetId(asset.Id) && history.Take(10).Any(s => s?.AssetId == asset.Id)) return null;
        // Stay in the nominated intent; never substitute an unrelated emotion to fill a quota.
        var choices = assets.Where(a => a.Intent == candidate.Intent && a.External == asset.External
            && !history.Take(10).Any(s => s?.AssetId == a.Id)).ToArray();
        if (choices.Length == 0) return null;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("sticker-v1\0" + deliveryKey));
        var sample = BinaryPrimitives.ReadUInt32LittleEndian(hash) / ((double)uint.MaxValue + 1);
        var probability = (candidate.Salience == "high" ? .75 : .45) * (asset.External ? .25 : 1);
        if (sample >= probability) return null;
        var selected = choices.FirstOrDefault(a => a.Id == asset.Id) ?? choices[hash[4] % choices.Length];
        return new(selected.Id, selected.Intent, candidate.Position);
    }

    public static string BuildPrompt(IReadOnlyList<StickerAsset>? catalog = null) => """
        可选本地图文表情：只在关系性的反应瞬间、图片比文字更有表达力时，才可提名一张；默认省略 sticker。
        不按关键词触发，不以发图维持聊天，不把 emotion 自动变成表情包。不要修改事实、立场或任务承诺。
        工具结果、代码/架构长分析、精确信息、严肃敏感话题、用户纠正你、必要澄清、已有信息配图时不得提名。
        外部角色图仅用于轻松玩笑、意外和夸张反应；不得代替 Mash 自己的关心、安慰、不同意、睡前回应或承诺。
        仅在最终 JSON 中可增加 sticker: {"assetId":"目录ID","intent":"目录intent","utility":"replace或enhance","salience":"medium或high","context":"social或playful","position":"before或after"}。
        图片只是重复文字、拿不准或没有匹配素材时省略；不要输出路径、链接、表情包标记或发图解释。
        宿主可能因预算等原因不展示提名图片，因此 text 必须独立完整、自然，不依赖图片传递必要信息；不支持纯表情回复。
        以下为素材目录（只选含义匹配者；创作、敬礼等图片不表示实际执行）：
        网络收藏也按 external 使用，其含义参见本轮目录数据；收藏成功不等于本轮必须发送。
        """ + "\n" + string.Join('\n', (catalog ?? StickerCatalog.Assets).Where(a => !IsWebAssetId(a.Id))
            .Concat((catalog ?? StickerCatalog.Assets).Where(a => IsWebAssetId(a.Id)).TakeLast(40)).Select(a => IsWebAssetId(a.Id)
            ? $"{a.Id} | {a.Intent} | 网络收藏（external）" : $"{a.Id} | {a.Intent} | {a.Label} | {a.Guidance}"));
}
