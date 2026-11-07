namespace FgoPet.App.Dialogue;

/// <summary>Reviewed local-only asset allowlist. Artwork is neither embedded nor redistributed.</summary>
public static class StickerCatalog
{
    public static IReadOnlyList<StickerAsset> Assets { get; } = Array.AsReadOnly(new StickerAsset[]
    {
        new("mash.happy", "happy", "开心", "happy.png", "ECBAAB8D688B599D0E9706F0FAF9D3F191317885ECAA83F8E6633D27F865979A", false, "开心、庆祝具体好消息"),
        new("mash.listening", "listening", "倾听", "listening.png", "693D517AC4F18B3D8D4972269DFE62C47B0C5A7E2519FF7EA596E98A3BAEDE3E", false, "耐心听着、轻轻接住"),
        new("mash.puzzled", "confused", "疑惑", "refusal.png", "EDC05F6B1CE405226264463D316B6056C16BB03559A16C192D38C83E1D5F1C12", false, "疑惑歪头；并非拒绝"),
        new("mash.refuse", "refuse", "不同意", "refuse.png", "84123E5BBE5486612758F88919F347A1BCF43A60DC15A602C7DA075AC6B397EB", false, "交叉手势，明确不同意"),
        new("mash.salute", "ack", "敬礼", "salute.png", "2A6896FEA98E523BFBEC2ACA9A096981134E1DFDBD6150629EBBCC9FACE2AB6D", false, "轻松敬礼、收到；不代表执行完成"),
        new("mash.shine", "excited", "眼前一亮", "shine.png", "9D12FFD8D62ED9D75DDE5727D20823918E3EF3286A5EA32076D801103E386780", false, "眼前一亮、期待"),
        new("mash.shocked", "surprise", "惊讶", "shocked.png", "5EB81E6B2020BCB12A8D043780CA70FA1256218665CAC5D6906A2E27D58DAC6E", false, "惊讶、意料之外"),
        new("mash.sleepy", "sleepy", "困倦", "sleepy.png", "0A2F30722BECEF0A4ABDF3A30DAF40B42828FFA8C6C9ED65A6C593BB0B7870A5", false, "困倦、轻松道晚安"),
        new("mash.staring", "skeptical", "凝视", "staring.png", "BB2A9164575FBCC8317F409054E059DB774F44287F6B927C9F0303108701173F", false, "无言凝视、轻微吐槽"),
        new("mash.sigh", "tired", "叹气", "tired_sigh.png", "259B93A8D663E30B4B4AA958A47EFB296B0CF607E912320FF1CF0A2BEE7CC8BB", false, "疲惫叹气；不诊断或嘲笑用户"),
        new("external.block", "annoyed", "玩笑式不满", "静态表情包（原版）/Block!.png", "35043B8764653B702CB2E8F49CA2E726E893E0127D5FB5A0F18AD6B786F81F2A", true, "外部角色：玩笑式不满，不用于真实拒绝"),
        new("external.love", "playful", "爱心", "静态表情包（原版）/Love.png", "14676C5E94C77AB85B7A9A164848326B006CEE41AA2DDDEBFE5B9A0B5CD95D24", true, "外部角色：夸张爱心梗，不表达关系承诺"),
        new("external.disbelief", "surprise", "不会吧", "静态表情包（原版）/不会吧？.png", "4FF5FE91E9120B3541B28415E7C811F9D00738B32F0A10E1576DB7AEBEA05E01", true, "外部角色：不敢置信"),
        new("external.peace", "playful", "不要吵架", "静态表情包（原版）/不要吵架.png", "08775B9D556339ADA43E16990425A74D59196AF1022853AF2CC97D4EC5B55889", true, "外部角色：玩笑式劝和，不介入真实冲突"),
        new("external.why", "surprise", "为什么", "静态表情包（原版）/为什么！.png", "5B65A0B587C554BDBD69597459FF44CF5454A1C547EF041876873FC058463667", true, "外部角色：夸张的为什么"),
        new("external.waking", "confused", "刚睡醒", "静态表情包（原版）/刚睡醒.png", "CA3F2B7219DED0E07B0BC01E97F58D97AA82D1A9AC5B619AFF0BFCD6BA3A81A3", true, "外部角色：迷糊、刚反应过来，不用于睡前关心"),
        new("mash.original-working", "curious", "看书", "静态表情包（原版）/创作中.png", "6B26450D2052927F90D7A255DA57BD2E6FCE66927A119B5FC790CDABE9479269", false, "Mash 看书、专注；不表示后台工作"),
        new("external.message", "playful", "看手机", "静态表情包（原版）/发送消息.png", "BAF184F994EBFC29739D17AE082D193D0B78B20ECD406EDD69BA6F454AF5DBEF", true, "外部角色：躺着看手机，不表示已发消息"),
        new("external.huh", "confused", "哈？", "静态表情包（原版）/哈？.png", "11BA8B925FBABA57082E0A6007F6A435D888129E42A41970931FC155A0D79D0B", true, "外部角色：哈？、荒唐玩笑"),
        new("external.cry", "sad", "哭哭", "静态表情包（原版）/大哭.png", "E724C1F2CC2842DC98E5FE5185F431C48591E14D04ED3EA20B5E14977BCD86AE", true, "外部角色：夸张哭哭梗，不用于真实痛苦"),
        new("mash.original-shy", "shy", "害羞", "静态表情包（原版）/害羞.png", "720B30126DCCDF9311BE841A42CFA1C99633538EC5EC37F42053E2DB05DBA7F3", false, "Mash 红脸、害羞地接住夸奖"),
        new("external.gloom", "tired", "忧郁", "静态表情包（原版）/忧郁.png", "0855E4E0EC9C64EBB8BDE88D61D719A1B8FFC9B696B8602FEFD32AEC9FF887A7", true, "外部角色：抱膝低落梗，不用于安慰"),
        new("external.parfait", "playful", "抹茶芭菲", "静态表情包（原版）/抹茶芭菲.png", "70BC87E633CDBA74840DBCDA0288172E5DE9F13A72A515A1FBDE391CDE42D269", true, "外部角色：甜点、抹茶芭菲的玩笑"),
        new("external.peek", "curious", "探头", "静态表情包（原版）/探头.png", "24366F349D8445B99D7A06EDEC8D9F3054362013F4A80840DD0DDB3224058145", true, "外部角色：探头、看看热闹"),
        new("external.interested", "playful", "坏笑", "静态表情包（原版）/有趣的女人.png", "69BEC53EF393897D230733D901D8AC641C7AF873F70397DD8E1B0C5785C698AE", true, "外部角色：坏笑、调侃；避免冒犯或性化"),
        new("external.leaving", "playful", "溜了溜了", "静态表情包（原版）/溜了溜了.png", "95FAAEDB6DA1727518BED96E6CF9779E36237C7879CE7880897811AA670CCDA7", true, "外部角色：溜了梗，不表示中止用户任务"),
        new("mash.original-angry", "annoyed", "鼓脸生气", "静态表情包（原版）/生气.png", "02D3EE6D24825F1C6250C104B8871FD0F6BD0706802F67FD8CE62201A96E4C49", false, "Mash 鼓脸生气；只限轻松调侃"),
        new("external.inspect", "curious", "让我看看", "静态表情包（原版）/让我看看.png", "F35BF6D0D3956543FDFAFA40EB38AFF6DE57514E862E11191313D48984CBACFB", true, "外部角色：让我看看、凑近看"),
        new("external.order", "playful", "请点单", "静态表情包（原版）/请点单.png", "03920181E270E1E13435C0707B7CADD7EE6F525D494F2A35453EE1ED18B668BA", true, "外部角色：点单玩笑，不表示真实下单"),
        new("external.me", "confused", "那我呢？", "静态表情包（原版）/那我呢？.png", "FD3E02D3B5A0121D38D9FBEDE8E4A6F72631A288452D86A65404D1A1B45DE0CF", true, "外部角色：那我呢？、玩笑式委屈"),
    });

    public static StickerAsset? Find(string? id) => id is { Length: > 0 and <= 64 }
        ? Assets.FirstOrDefault(asset => asset.Id == id) : null;
}
