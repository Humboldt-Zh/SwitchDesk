using SwitchDesk.Core;

namespace SwitchDesk.App;

public sealed class CrossSaveGameItem(CrossSaveGameDefinition definition) : Observable
{
    public CrossSaveGameDefinition Definition { get; } = definition;
    public string AdapterId => Definition.AdapterId;
    public string Name => Definition.Name;
    public string Initial => Name[..1];
    public string SupportStatus => "比较与备份 · 迁移尚未准入";
    private string pcStatus = "尚未扫描电脑存档", switchStatus = "未连接 Switch";
    public string PcStatus { get => pcStatus; set => Set(ref pcStatus, value); }
    public string SwitchStatus { get => switchStatus; set => Set(ref switchStatus, value); }
}

public sealed record CrossSaveTargetGame(string TitleId, string Name)
{
    public string Label => Name + " · " + TitleId;
}

public sealed class CrossSaveViewModel : Observable
{
    private bool open, canMigrate, canSelect = true, filesLoading, filesReady;
    private CrossSaveGameItem? selectedGame;
    private CrossSaveTargetGame[] targetGames = []; private CrossSaveTargetGame? selectedTargetGame;
    private SaveChoice[] users = []; private SaveChoice? selectedUser;
    private PcSaveCandidate[] candidates = []; private PcSaveCandidate? selectedPc;
    private string[] switchFiles = []; private CrossSaveFile[] switchFileOptions = []; private string? selectedSwitchFile;
    private string pcSummary = "选择有效电脑存档后读取", switchSummary = "选择 Switch 游戏和用户后，自动读取槽位", comparison = "先选择两端存档，再读取并比较", gate = "仅通过版本准入的组合可以迁移；未知组合仍可备份";
    private string fileStatus = "选择 Switch 游戏和用户后，自动读取槽位";
    public IReadOnlyList<CrossSaveGameItem> Games { get; } = CrossSaveGames.All.Select(x => new CrossSaveGameItem(x)).ToArray();
    public CrossSaveGameItem? SelectedGame
    {
        get => selectedGame;
        set
        {
            if (!Set(ref selectedGame, value)) return;
            Changed(nameof(HasSelection)); Changed(nameof(Adapter));
            TargetGames = []; SelectedTargetGame = null; Users = []; SelectedUser = null;
            ClearFiles(value?.Definition.HasSwitchIdentity == false ? "Switch 游戏识别待补；此游戏当前可发现和备份电脑存档" : "选择 Switch 游戏和用户后，自动读取槽位");
            Invalidate(); SelectionChanged?.Invoke();
        }
    }
    public bool HasSelection => SelectedGame != null;
    // Retained for existing rendering callers; the page itself uses HasSelection and shell navigation.
    public bool Open { get => open; set => Set(ref open, value); }
    public bool CanMigrate { get => canMigrate; set { Set(ref canMigrate, value); System.Windows.Input.CommandManager.InvalidateRequerySuggested(); } }
    public bool CanSelect { get => canSelect; set { if (Set(ref canSelect, value)) Changed(nameof(CanChooseFile)); } }
    public bool FilesLoading { get => filesLoading; set { if (Set(ref filesLoading, value)) Changed(nameof(CanChooseFile)); } }
    public bool FilesReady { get => filesReady; private set => Set(ref filesReady, value); }
    public bool CanChooseFile => CanSelect && !FilesLoading && switchFiles.Length > 0;
    public IReadOnlyList<ISaveAdapter> Adapters => SaveAdapters.All;
    public ISaveAdapter? Adapter { get => SelectedGame?.Definition.Adapter; set => SelectedGame = Games.SingleOrDefault(x => x.AdapterId == value?.Id); }
    public CrossSaveTargetGame[] TargetGames { get => targetGames; set => Set(ref targetGames, value); }
    public CrossSaveTargetGame? SelectedTargetGame
    {
        get => selectedTargetGame;
        set
        {
            if (!Set(ref selectedTargetGame, value)) return;
            Users = []; SelectedUser = null; ClearFiles("选择 Switch 用户后，自动读取槽位");
            Invalidate(); TargetChanged?.Invoke();
        }
    }
    public SaveChoice[] Users { get => users; set => Set(ref users, value); }
    public SaveChoice? SelectedUser
    {
        get => selectedUser;
        set
        {
            if (!Set(ref selectedUser, value)) return;
            ClearFiles("正在准备读取 Switch 槽位"); Invalidate(); UserChanged?.Invoke();
        }
    }
    public PcSaveCandidate[] Candidates { get => candidates; set => Set(ref candidates, value); }
    public PcSaveCandidate? SelectedPc { get => selectedPc; set { if (Set(ref selectedPc, value)) { PcSummary = value == null ? "选择有效电脑存档后读取" : value.Label + "\n读取前请退出游戏"; Invalidate(); } } }
    public string[] SwitchFiles
    {
        get => switchFiles;
        set => SetFiles(value.Select(x => new CrossSaveFile(x, 1)).ToArray());
    }
    public CrossSaveFile[] SwitchFileOptions => switchFileOptions;
    public string? SelectedSwitchFile
    {
        get => selectedSwitchFile;
        set
        {
            var valid = value != null && switchFiles.Contains(value, StringComparer.Ordinal) ? value : null;
            if (Set(ref selectedSwitchFile, valid))
            {
                SwitchSummary = valid == null ? "尚未读取当前选择的存档内容" : $"{SelectedUser?.Name} · {valid}\n尚未读取当前选择的存档内容";
                Invalidate();
            }
        }
    }
    public string FileStatus { get => fileStatus; set => Set(ref fileStatus, value); }
    public string PcSummary { get => pcSummary; set => Set(ref pcSummary, value); }
    public string SwitchSummary { get => switchSummary; set => Set(ref switchSummary, value); }
    public string Comparison { get => comparison; set => Set(ref comparison, value); }
    public string Gate { get => gate; set => Set(ref gate, value); }
    public event Action? SelectionChanged;
    public event Action? TargetChanged;
    public event Action? UserChanged;
    public event Action? PreviewInvalidated;
    public void SetFiles(CrossSaveFile[] files)
    {
        switchFileOptions = files; switchFiles = files.Where(x => x.CanCompare).Select(x => x.Path).ToArray();
        FilesReady = true;
        Changed(nameof(SwitchFiles)); Changed(nameof(SwitchFileOptions)); Changed(nameof(CanChooseFile));
        SelectedSwitchFile = switchFiles.Contains(selectedSwitchFile, StringComparer.Ordinal) ? selectedSwitchFile : switchFiles.Length == 1 ? switchFiles[0] : null;
    }
    public void ClearFiles(string status)
    {
        SetFiles([]); FilesReady = false; FilesLoading = false; FileStatus = status; CanMigrate = false;
        SwitchSummary = "尚未读取当前选择的存档内容";
    }
    public void Invalidate()
    {
        CanMigrate = false; Comparison = "先选择两端存档，再读取并比较"; Gate = "当前没有可写入的迁移预览";
        PreviewInvalidated?.Invoke();
    }
}
