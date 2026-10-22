using System.Globalization;

namespace QianShi.PaperTodo.Archive;

/// <summary>
/// User-visible text for this plugin. PaperTodo localizes its own strings, but a native plugin is
/// responsible for its own surface, so every string the management card, popups and context menus
/// show comes from here. Languages follow the host UI culture the runtime reports.
/// </summary>
public static class ArchiveText
{
    private const string FallbackLanguage = "en";

    // Lazy on purpose: static field initializers run in textual order, and these catalogs are
    // declared below. Eager initialization here would capture four null catalogs.
    private static Dictionary<string, Dictionary<string, string>>? _catalogs;
    private static Dictionary<string, string>? _current;

    private static Dictionary<string, Dictionary<string, string>> Catalogs =>
        _catalogs ??= new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["zh"] = Chinese,
            ["en"] = English,
            ["ja"] = Japanese,
            ["ko"] = Korean
        };

    private static Dictionary<string, string> Current => _current ??= English;

    private static string _language = FallbackLanguage;

    /// <summary>Normalized two-letter language currently in use, for example "zh".</summary>
    public static string Language => _language;

    /// <summary>Selects the catalog from a host culture name such as zh-CN or en-US.</summary>
    public static void Configure(string? uiLanguage)
    {
        var normalized = Normalize(uiLanguage);
        _language = Catalogs.ContainsKey(normalized) ? normalized : FallbackLanguage;
        _current = Catalogs[_language];
    }

    public static bool UsesLanguage(string? uiLanguage) =>
        string.Equals(Normalize(uiLanguage), _language, StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? uiLanguage)
    {
        if (string.IsNullOrWhiteSpace(uiLanguage))
        {
            return FallbackLanguage;
        }

        var trimmed = uiLanguage.Trim();
        var separator = trimmed.IndexOfAny(['-', '_']);
        return (separator > 0 ? trimmed[..separator] : trimmed).ToLowerInvariant();
    }

    /// <summary>Raw template for a key. Unknown keys resolve to English, then to the key itself.</summary>
    public static string Get(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }

        if (Current.TryGetValue(key, out var value))
        {
            return value;
        }

        if (English.TryGetValue(key, out var fallback))
        {
            return fallback;
        }

        return key;
    }

    public static string Format(string key, params object?[] args)
    {
        var template = Get(key);
        if (args.Length == 0)
        {
            return template;
        }

        try
        {
            return string.Format(CultureInfo.CurrentCulture, template, args);
        }
        catch (FormatException)
        {
            return template;
        }
    }

    /// <summary>Localized message for an <see cref="ArchiveResultCodes"/> value.</summary>
    public static string ForResult(string code, string? detail = null)
    {
        var key = "Result." + code;
        var template = Get(key);
        if (string.Equals(template, key, StringComparison.Ordinal))
        {
            // Unknown code: never show a raw identifier to the user.
            template = Get("Result.err.unknown");
        }

        return string.IsNullOrEmpty(detail)
            ? template
            : Format("ResultWithDetail", template, detail);
    }

    public static string ForOrigin(ArchiveCaptureOrigin origin) => origin switch
    {
        ArchiveCaptureOrigin.User => Get("OriginUser"),
        ArchiveCaptureOrigin.Mcp => Get("OriginMcp"),
        ArchiveCaptureOrigin.Plugin => Get("OriginPlugin"),
        ArchiveCaptureOrigin.System => Get("OriginSystem"),
        ArchiveCaptureOrigin.Import => Get("OriginImport"),
        _ => Get("OriginUnknown")
    };

    public static string ForFilter(ArchiveListFilter filter) => filter switch
    {
        ArchiveListFilter.Archived => Get("TabArchived"),
        ArchiveListFilter.Trashed => Get("TabTrash"),
        ArchiveListFilter.Restored => Get("TabRestored"),
        _ => Get("TabAll")
    };

    private static readonly Dictionary<string, string> Chinese = new(StringComparer.Ordinal)
    {
        ["AppTitle"] = "归档中心",
        ["AppSubtitle"] = "归档只从界面移除卡片，数据始终留在本地，可随时恢复。",
        ["TabAll"] = "全部",
        ["TabArchived"] = "已归档",
        ["TabTrash"] = "回收站",
        ["TabRestored"] = "已恢复",
        ["SearchPlaceholder"] = "搜索标题或内容…",
        ["ButtonRestore"] = "恢复",
        ["ButtonPurge"] = "彻底删除",
        ["ButtonRestoreAll"] = "全部恢复",
        ["ButtonOpenCenter"] = "打开归档中心",
        ["ButtonRefresh"] = "刷新",
        ["ButtonClose"] = "关闭",
        ["EmptyAll"] = "还没有归档记录。",
        ["EmptyArchived"] = "没有已归档的卡片。",
        ["EmptyTrash"] = "回收站是空的。",
        ["EmptyRestored"] = "还没有恢复记录。",
        ["StatArchived"] = "已归档",
        ["StatTrash"] = "回收站",
        ["StatRestored"] = "已恢复",
        ["StatMirror"] = "已备份卡片",
        ["ConfirmPurgeTitle"] = "彻底删除？",
        ["ConfirmPurgeBody"] = "「{0}」将被永久删除，且无法恢复。",
        ["Confirm"] = "确定",
        ["Cancel"] = "取消",
        ["BadgeTodo"] = "待办",
        ["BadgeNote"] = "笔记",
        ["BadgeArchived"] = "已归档",
        ["BadgeTrashed"] = "已删除",
        ["BadgeRestored"] = "已恢复",
        ["CapturedAt"] = "记录于 {0}",
        ["RestoredAt"] = "恢复于 {0}",
        ["StoragePath"] = "存储位置",
        ["HintArchive"] = "提示：点击卡片顶栏的归档按钮，即可把卡片从界面移除。",
        ["HintTrash"] = "在 PaperTodo 里直接删除的卡片会留在这里，可一键找回。",
        ["HintNotRestorable"] = "这张卡片的正文由其他插件提供，无法读回内容。",
        ["HintNoteImages"] = "笔记里的图片无法恢复，正文文字可以。",
        ["HintContentPartial"] = "这是删除前的最后一次备份，可能缺少最后几次输入。",
        ["TopBarArchiveTooltip"] = "归档此卡片",
        ["TopBarOpenCenterTooltip"] = "打开归档中心",
        ["MenuArchive"] = "归档此卡片",
        ["MenuOpenCenter"] = "打开归档中心",
        ["MenuUnarchive"] = "取消归档",
        ["CapsuleText"] = "归档 {0}",
        ["ContentNoteLength"] = "笔记正文 {0} 字",
        ["ContentTodoCount"] = "{0} 个待办 · 已完成 {1}",
        ["OriginUser"] = "用户",
        ["OriginMcp"] = "MCP",
        ["OriginPlugin"] = "插件",
        ["OriginSystem"] = "系统",
        ["OriginImport"] = "导入",
        ["OriginUnknown"] = "未知",
        ["ReasonHostDelete"] = "在 PaperTodo 中被删除",
        ["ReasonUserArchive"] = "手动归档",
        ["ReasonTodoDelete"] = "待办条目被删除",
        ["Loading"] = "正在载入…",
        ["ResultWithDetail"] = "{0}（{1}）",
        ["Result.ok.archive.paper"] = "卡片已归档",
        ["Result.ok.archive.todo"] = "待办已归档",
        ["Result.ok.restore"] = "已恢复",
        ["Result.ok.restoreAll"] = "已全部恢复",
        ["Result.ok.purge"] = "已彻底删除",
        ["Result.ok.openCenter"] = "已打开归档中心",
        ["Result.err.already_archived"] = "这张卡片已经归档",
        ["Result.err.not_archived"] = "这张卡片没有归档",
        ["Result.err.not_found"] = "找不到对应的记录",
        ["Result.err.self_paper"] = "不能归档归档中心本身",
        ["Result.err.plugin_paper"] = "插件自己的卡片不能归档",
        ["Result.err.permission"] = "缺少所需权限，请重新安装插件",
        ["Result.err.not_restorable"] = "这条记录无法恢复",
        ["Result.err.content_missing"] = "没有可恢复的内容",
        ["Result.err.create_failed"] = "恢复失败，PaperTodo 拒绝创建卡片",
        ["Result.err.presentation_unavailable"] = "当前无法显示这张卡片",
        ["Result.err.nothing_to_do"] = "没有需要处理的记录",
        ["Result.err.storage"] = "归档存储读写失败",
        ["Result.err.unknown"] = "操作失败",
        ["MetaSeparator"] = " · ",
        ["Untitled"] = "未命名",
        ["FromPaper"] = "来自「{0}」",
        ["EmptySearch"] = "没有匹配的记录。",
        ["ResultCount"] = "共 {0} 条",
        ["ListTruncated"] = "（仅显示最近 {0} 条）",
        ["StorageHint"] = "数据保存在本地插件目录，不会上传。",
        ["StorageWarning"] = "归档存储报告了读写问题，列表可能不完整。",
        ["ButtonClearSearch"] = "清除",
        ["ConfirmDangerHint"] = "此操作无法撤销。",
        ["CapsuleTrash"] = "回收站 {0}",
        ["CapsuleCounts"] = "已归档 {0} · 回收站 {1}",
        ["DeleteWarningTitle"] = "请勿删除这张卡片",
        ["DeleteWarningEffect"] = "删除后顶栏按钮、卡片右键菜单和待办行菜单都会消失。",
        ["DeleteWarningSafe"] = "归档数据不会丢失，仍然保存在插件目录里。",
        ["DeleteWarningRecoverMenu"] = "找回：右键任意笔记卡片 →「正文类型」→「卡片归档」。",
        ["DeleteWarningRecoverRestart"] = "或者重新启动 PaperTodo。"
    };

    private static readonly Dictionary<string, string> English = new(StringComparer.Ordinal)
    {
        ["AppTitle"] = "Archive Center",
        ["AppSubtitle"] = "Archiving only removes a card from the interface. The data stays local and can always be restored.",
        ["TabAll"] = "All",
        ["TabArchived"] = "Archived",
        ["TabTrash"] = "Recycle bin",
        ["TabRestored"] = "Restored",
        ["SearchPlaceholder"] = "Search title or content…",
        ["ButtonRestore"] = "Restore",
        ["ButtonPurge"] = "Delete forever",
        ["ButtonRestoreAll"] = "Restore all",
        ["ButtonOpenCenter"] = "Open Archive Center",
        ["ButtonRefresh"] = "Refresh",
        ["ButtonClose"] = "Close",
        ["EmptyAll"] = "No archive records yet.",
        ["EmptyArchived"] = "No archived cards.",
        ["EmptyTrash"] = "The recycle bin is empty.",
        ["EmptyRestored"] = "No restored records yet.",
        ["StatArchived"] = "Archived",
        ["StatTrash"] = "Recycle bin",
        ["StatRestored"] = "Restored",
        ["StatMirror"] = "Backed up cards",
        ["ConfirmPurgeTitle"] = "Delete forever?",
        ["ConfirmPurgeBody"] = "\"{0}\" is permanently deleted and cannot be recovered.",
        ["Confirm"] = "OK",
        ["Cancel"] = "Cancel",
        ["BadgeTodo"] = "Todo",
        ["BadgeNote"] = "Note",
        ["BadgeArchived"] = "Archived",
        ["BadgeTrashed"] = "Deleted",
        ["BadgeRestored"] = "Restored",
        ["CapturedAt"] = "Captured {0}",
        ["RestoredAt"] = "Restored {0}",
        ["StoragePath"] = "Storage",
        ["HintArchive"] = "Tip: use the archive button on a card's top bar to remove it from the interface.",
        ["HintTrash"] = "Cards deleted directly in PaperTodo are kept here and can be recovered.",
        ["HintNotRestorable"] = "This card's body is provided by another plugin, so its content cannot be read back.",
        ["HintNoteImages"] = "Images inside a note cannot be restored; the text can.",
        ["HintContentPartial"] = "This is the last backup before deletion and may miss the final keystrokes.",
        ["TopBarArchiveTooltip"] = "Archive this card",
        ["TopBarOpenCenterTooltip"] = "Open Archive Center",
        ["MenuArchive"] = "Archive this card",
        ["MenuOpenCenter"] = "Open Archive Center",
        ["MenuUnarchive"] = "Unarchive",
        ["CapsuleText"] = "Archive {0}",
        ["ContentNoteLength"] = "Note body, {0} characters",
        ["ContentTodoCount"] = "{0} todos · {1} done",
        ["OriginUser"] = "User",
        ["OriginMcp"] = "MCP",
        ["OriginPlugin"] = "Plugin",
        ["OriginSystem"] = "System",
        ["OriginImport"] = "Import",
        ["OriginUnknown"] = "Unknown",
        ["ReasonHostDelete"] = "Deleted in PaperTodo",
        ["ReasonUserArchive"] = "Archived manually",
        ["ReasonTodoDelete"] = "Todo row deleted",
        ["Loading"] = "Loading…",
        ["ResultWithDetail"] = "{0} ({1})",
        ["Result.ok.archive.paper"] = "Card archived",
        ["Result.ok.archive.todo"] = "Todo archived",
        ["Result.ok.restore"] = "Restored",
        ["Result.ok.restoreAll"] = "All restored",
        ["Result.ok.purge"] = "Permanently deleted",
        ["Result.ok.openCenter"] = "Archive Center opened",
        ["Result.err.already_archived"] = "This card is already archived",
        ["Result.err.not_archived"] = "This card is not archived",
        ["Result.err.not_found"] = "The record could not be found",
        ["Result.err.self_paper"] = "The Archive Center cannot archive itself",
        ["Result.err.plugin_paper"] = "A plugin-owned card cannot be archived",
        ["Result.err.permission"] = "Required permission is missing; reinstall the plugin",
        ["Result.err.not_restorable"] = "This record cannot be restored",
        ["Result.err.content_missing"] = "There is no content to restore",
        ["Result.err.create_failed"] = "Restore failed because PaperTodo refused to create the card",
        ["Result.err.presentation_unavailable"] = "This card cannot be shown right now",
        ["Result.err.nothing_to_do"] = "There is nothing to process",
        ["Result.err.storage"] = "Archive storage read or write failed",
        ["Result.err.unknown"] = "The operation failed",
        ["MetaSeparator"] = " · ",
        ["Untitled"] = "Untitled",
        ["FromPaper"] = "From \"{0}\"",
        ["EmptySearch"] = "No records match your search.",
        ["ResultCount"] = "{0} records",
        ["ListTruncated"] = "(showing the newest {0})",
        ["StorageHint"] = "Data stays in the local plugin folder and is never uploaded.",
        ["StorageWarning"] = "Archive storage reported a read/write problem; this list may be incomplete.",
        ["ButtonClearSearch"] = "Clear",
        ["ConfirmDangerHint"] = "This action cannot be undone.",
        ["CapsuleTrash"] = "Bin {0}",
        ["CapsuleCounts"] = "{0} archived · {1} in bin",
        ["DeleteWarningTitle"] = "Do not delete this card",
        ["DeleteWarningEffect"] = "Deleting it removes the top bar button, the card context menu and the todo row menu.",
        ["DeleteWarningSafe"] = "No archive data is lost; it stays in the plugin folder.",
        ["DeleteWarningRecoverMenu"] = "To get it back: right-click any note card → \"Body type\" → \"卡片归档\".",
        ["DeleteWarningRecoverRestart"] = "Or restart PaperTodo."
    };

    private static readonly Dictionary<string, string> Japanese = new(StringComparer.Ordinal)
    {
        ["AppTitle"] = "アーカイブセンター",
        ["AppSubtitle"] = "アーカイブはカードを画面から外すだけで、データはローカルに残り、いつでも復元できます。",
        ["TabAll"] = "すべて",
        ["TabArchived"] = "アーカイブ済み",
        ["TabTrash"] = "ゴミ箱",
        ["TabRestored"] = "復元済み",
        ["SearchPlaceholder"] = "タイトルや内容を検索…",
        ["ButtonRestore"] = "復元",
        ["ButtonPurge"] = "完全に削除",
        ["ButtonRestoreAll"] = "すべて復元",
        ["ButtonOpenCenter"] = "アーカイブセンターを開く",
        ["ButtonRefresh"] = "更新",
        ["ButtonClose"] = "閉じる",
        ["EmptyAll"] = "アーカイブ記録はまだありません。",
        ["EmptyArchived"] = "アーカイブ済みのカードはありません。",
        ["EmptyTrash"] = "ゴミ箱は空です。",
        ["EmptyRestored"] = "復元した記録はまだありません。",
        ["StatArchived"] = "アーカイブ済み",
        ["StatTrash"] = "ゴミ箱",
        ["StatRestored"] = "復元済み",
        ["StatMirror"] = "バックアップ済み",
        ["ConfirmPurgeTitle"] = "完全に削除しますか？",
        ["ConfirmPurgeBody"] = "「{0}」は完全に削除され、復元できません。",
        ["Confirm"] = "OK",
        ["Cancel"] = "キャンセル",
        ["BadgeTodo"] = "ToDo",
        ["BadgeNote"] = "ノート",
        ["BadgeArchived"] = "アーカイブ済み",
        ["BadgeTrashed"] = "削除済み",
        ["BadgeRestored"] = "復元済み",
        ["CapturedAt"] = "記録日時 {0}",
        ["RestoredAt"] = "復元日時 {0}",
        ["StoragePath"] = "保存場所",
        ["HintArchive"] = "ヒント：カードのトップバーのアーカイブボタンで画面から外せます。",
        ["HintTrash"] = "PaperTodo で直接削除したカードはここに残り、復元できます。",
        ["HintNotRestorable"] = "このカードの本文は別のプラグインが提供しているため読み戻せません。",
        ["HintNoteImages"] = "ノート内の画像は復元できません。本文テキストは復元できます。",
        ["HintContentPartial"] = "削除直前の最後のバックアップです。最後の入力が含まれない場合があります。",
        ["TopBarArchiveTooltip"] = "このカードをアーカイブ",
        ["TopBarOpenCenterTooltip"] = "アーカイブセンターを開く",
        ["MenuArchive"] = "このカードをアーカイブ",
        ["MenuOpenCenter"] = "アーカイブセンターを開く",
        ["MenuUnarchive"] = "アーカイブを解除",
        ["CapsuleText"] = "アーカイブ {0}",
        ["ContentNoteLength"] = "ノート本文 {0} 文字",
        ["ContentTodoCount"] = "{0} 件の ToDo · 完了 {1}",
        ["OriginUser"] = "ユーザー",
        ["OriginMcp"] = "MCP",
        ["OriginPlugin"] = "プラグイン",
        ["OriginSystem"] = "システム",
        ["OriginImport"] = "インポート",
        ["OriginUnknown"] = "不明",
        ["ReasonHostDelete"] = "PaperTodo で削除",
        ["ReasonUserArchive"] = "手動アーカイブ",
        ["ReasonTodoDelete"] = "ToDo 行が削除されました",
        ["Loading"] = "読み込み中…",
        ["ResultWithDetail"] = "{0}（{1}）",
        ["Result.ok.archive.paper"] = "カードをアーカイブしました",
        ["Result.ok.archive.todo"] = "ToDo をアーカイブしました",
        ["Result.ok.restore"] = "復元しました",
        ["Result.ok.restoreAll"] = "すべて復元しました",
        ["Result.ok.purge"] = "完全に削除しました",
        ["Result.ok.openCenter"] = "アーカイブセンターを開きました",
        ["Result.err.already_archived"] = "このカードはすでにアーカイブ済みです",
        ["Result.err.not_archived"] = "このカードはアーカイブされていません",
        ["Result.err.not_found"] = "記録が見つかりません",
        ["Result.err.self_paper"] = "アーカイブセンター自体はアーカイブできません",
        ["Result.err.plugin_paper"] = "プラグイン自身のカードはアーカイブできません",
        ["Result.err.permission"] = "必要な権限がありません。プラグインを再インストールしてください",
        ["Result.err.not_restorable"] = "この記録は復元できません",
        ["Result.err.content_missing"] = "復元できる内容がありません",
        ["Result.err.create_failed"] = "カードを作成できず復元に失敗しました",
        ["Result.err.presentation_unavailable"] = "現在このカードを表示できません",
        ["Result.err.nothing_to_do"] = "処理する記録がありません",
        ["Result.err.storage"] = "アーカイブの保存領域の読み書きに失敗しました",
        ["Result.err.unknown"] = "操作に失敗しました",
        ["MetaSeparator"] = " ・ ",
        ["Untitled"] = "無題",
        ["FromPaper"] = "「{0}」から",
        ["EmptySearch"] = "一致する記録はありません。",
        ["ResultCount"] = "{0} 件",
        ["ListTruncated"] = "（最新 {0} 件のみ表示）",
        ["StorageHint"] = "データはローカルのプラグインフォルダにのみ保存され、アップロードされません。",
        ["StorageWarning"] = "アーカイブ保存領域で読み書きの問題が発生しました。一覧が不完全な可能性があります。",
        ["ButtonClearSearch"] = "クリア",
        ["ConfirmDangerHint"] = "この操作は取り消せません。",
        ["CapsuleTrash"] = "ゴミ箱 {0}",
        ["CapsuleCounts"] = "アーカイブ {0} ・ ゴミ箱 {1}",
        ["DeleteWarningTitle"] = "このカードを削除しないでください",
        ["DeleteWarningEffect"] = "削除すると、トップバーのボタン、カードの右クリックメニュー、ToDo 行のメニューがすべて消えます。",
        ["DeleteWarningSafe"] = "アーカイブのデータは失われません。プラグインのフォルダに残ります。",
        ["DeleteWarningRecoverMenu"] = "復元方法：任意のノートカードを右クリック → 「本文の種類」→「卡片归档」。",
        ["DeleteWarningRecoverRestart"] = "または PaperTodo を再起動します。"
    };

    private static readonly Dictionary<string, string> Korean = new(StringComparer.Ordinal)
    {
        ["AppTitle"] = "보관 센터",
        ["AppSubtitle"] = "보관은 카드를 화면에서만 제거하며 데이터는 로컬에 남아 언제든 복원할 수 있습니다.",
        ["TabAll"] = "전체",
        ["TabArchived"] = "보관됨",
        ["TabTrash"] = "휴지통",
        ["TabRestored"] = "복원됨",
        ["SearchPlaceholder"] = "제목이나 내용 검색…",
        ["ButtonRestore"] = "복원",
        ["ButtonPurge"] = "완전 삭제",
        ["ButtonRestoreAll"] = "모두 복원",
        ["ButtonOpenCenter"] = "보관 센터 열기",
        ["ButtonRefresh"] = "새로 고침",
        ["ButtonClose"] = "닫기",
        ["EmptyAll"] = "아직 보관 기록이 없습니다.",
        ["EmptyArchived"] = "보관된 카드가 없습니다.",
        ["EmptyTrash"] = "휴지통이 비어 있습니다.",
        ["EmptyRestored"] = "아직 복원 기록이 없습니다.",
        ["StatArchived"] = "보관됨",
        ["StatTrash"] = "휴지통",
        ["StatRestored"] = "복원됨",
        ["StatMirror"] = "백업된 카드",
        ["ConfirmPurgeTitle"] = "완전히 삭제할까요?",
        ["ConfirmPurgeBody"] = "「{0}」 기록이 영구 삭제되며 복원할 수 없습니다.",
        ["Confirm"] = "확인",
        ["Cancel"] = "취소",
        ["BadgeTodo"] = "할 일",
        ["BadgeNote"] = "노트",
        ["BadgeArchived"] = "보관됨",
        ["BadgeTrashed"] = "삭제됨",
        ["BadgeRestored"] = "복원됨",
        ["CapturedAt"] = "{0}에 기록",
        ["RestoredAt"] = "{0}에 복원",
        ["StoragePath"] = "저장 위치",
        ["HintArchive"] = "팁: 카드 상단 바의 보관 버튼으로 화면에서 제거할 수 있습니다.",
        ["HintTrash"] = "PaperTodo에서 직접 삭제한 카드는 여기에 남아 복원할 수 있습니다.",
        ["HintNotRestorable"] = "이 카드의 본문은 다른 플러그인이 제공하므로 내용을 읽어올 수 없습니다.",
        ["HintNoteImages"] = "노트 안의 이미지는 복원할 수 없으며 본문 텍스트는 복원됩니다.",
        ["HintContentPartial"] = "삭제 직전의 마지막 백업이라 마지막 입력이 빠졌을 수 있습니다.",
        ["TopBarArchiveTooltip"] = "이 카드 보관",
        ["TopBarOpenCenterTooltip"] = "보관 센터 열기",
        ["MenuArchive"] = "이 카드 보관",
        ["MenuOpenCenter"] = "보관 센터 열기",
        ["MenuUnarchive"] = "보관 해제",
        ["CapsuleText"] = "보관 {0}",
        ["ContentNoteLength"] = "노트 본문 {0}자",
        ["ContentTodoCount"] = "할 일 {0}개 · 완료 {1}개",
        ["OriginUser"] = "사용자",
        ["OriginMcp"] = "MCP",
        ["OriginPlugin"] = "플러그인",
        ["OriginSystem"] = "시스템",
        ["OriginImport"] = "가져오기",
        ["OriginUnknown"] = "알 수 없음",
        ["ReasonHostDelete"] = "PaperTodo에서 삭제됨",
        ["ReasonUserArchive"] = "직접 보관",
        ["ReasonTodoDelete"] = "할 일 항목이 삭제됨",
        ["Loading"] = "불러오는 중…",
        ["ResultWithDetail"] = "{0} ({1})",
        ["Result.ok.archive.paper"] = "카드를 보관했습니다",
        ["Result.ok.archive.todo"] = "할 일을 보관했습니다",
        ["Result.ok.restore"] = "복원했습니다",
        ["Result.ok.restoreAll"] = "모두 복원했습니다",
        ["Result.ok.purge"] = "영구 삭제했습니다",
        ["Result.ok.openCenter"] = "보관 센터를 열었습니다",
        ["Result.err.already_archived"] = "이미 보관된 카드입니다",
        ["Result.err.not_archived"] = "보관되지 않은 카드입니다",
        ["Result.err.not_found"] = "기록을 찾을 수 없습니다",
        ["Result.err.self_paper"] = "보관 센터 자체는 보관할 수 없습니다",
        ["Result.err.plugin_paper"] = "플러그인 소유 카드는 보관할 수 없습니다",
        ["Result.err.permission"] = "필요한 권한이 없습니다. 플러그인을 다시 설치하세요",
        ["Result.err.not_restorable"] = "이 기록은 복원할 수 없습니다",
        ["Result.err.content_missing"] = "복원할 내용이 없습니다",
        ["Result.err.create_failed"] = "카드를 만들지 못해 복원에 실패했습니다",
        ["Result.err.presentation_unavailable"] = "지금은 이 카드를 표시할 수 없습니다",
        ["Result.err.nothing_to_do"] = "처리할 기록이 없습니다",
        ["Result.err.storage"] = "보관 저장소 읽기/쓰기에 실패했습니다",
        ["Result.err.unknown"] = "작업이 실패했습니다",
        ["MetaSeparator"] = " · ",
        ["Untitled"] = "제목 없음",
        ["FromPaper"] = "「{0}」에서",
        ["EmptySearch"] = "일치하는 기록이 없습니다.",
        ["ResultCount"] = "{0}개",
        ["ListTruncated"] = "(최근 {0}개만 표시)",
        ["StorageHint"] = "데이터는 로컬 플러그인 폴더에만 저장되며 업로드되지 않습니다.",
        ["StorageWarning"] = "보관 저장소에 읽기/쓰기 문제가 있어 목록이 불완전할 수 있습니다.",
        ["ButtonClearSearch"] = "지우기",
        ["ConfirmDangerHint"] = "이 작업은 되돌릴 수 없습니다.",
        ["CapsuleTrash"] = "휴지통 {0}",
        ["CapsuleCounts"] = "보관 {0} · 휴지통 {1}",
        ["DeleteWarningTitle"] = "이 카드를 삭제하지 마세요",
        ["DeleteWarningEffect"] = "삭제하면 상단 바 버튼, 카드 우클릭 메뉴, 할 일 행 메뉴가 모두 사라집니다.",
        ["DeleteWarningSafe"] = "보관 데이터는 사라지지 않고 플러그인 폴더에 그대로 남습니다.",
        ["DeleteWarningRecoverMenu"] = "되찾는 방법: 아무 노트 카드를 우클릭 → 「본문 유형」→「卡片归档」.",
        ["DeleteWarningRecoverRestart"] = "또는 PaperTodo를 다시 시작하세요."
    };
}
