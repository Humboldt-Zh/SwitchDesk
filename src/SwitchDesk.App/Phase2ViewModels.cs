using SwitchDesk.Core;

namespace SwitchDesk.App;

public sealed class CrossSaveViewModel : Observable
{
    private bool open, canMigrate; private ISaveAdapter? adapter;
    private PcSaveCandidate[] candidates = []; private PcSaveCandidate? selectedPc;
    private string[] switchFiles = []; private string? selectedSwitchFile;
    private string pcSummary = "选择有效电脑存档后读取", switchSummary = "选择 Switch 用户后读取", comparison = "先读取双端存档，再比较内容变化", gate = "仅通过版本准入的组合可以迁移；未知组合仍可备份";
    public bool Open { get => open; set => Set(ref open, value); }
    public bool CanMigrate { get => canMigrate; set { Set(ref canMigrate, value); System.Windows.Input.CommandManager.InvalidateRequerySuggested(); } }
    public IReadOnlyList<ISaveAdapter> Adapters => SaveAdapters.All;
    public ISaveAdapter? Adapter { get => adapter; set { if (Set(ref adapter, value)) SelectionChanged?.Invoke(); } }
    public PcSaveCandidate[] Candidates { get => candidates; set => Set(ref candidates, value); }
    public PcSaveCandidate? SelectedPc { get => selectedPc; set { if (Set(ref selectedPc, value)) Invalidate(); } }
    public string[] SwitchFiles { get => switchFiles; set => Set(ref switchFiles, value); }
    public string? SelectedSwitchFile { get => selectedSwitchFile; set { if (Set(ref selectedSwitchFile, value)) Invalidate(); } }
    public string PcSummary { get => pcSummary; set => Set(ref pcSummary, value); }
    public string SwitchSummary { get => switchSummary; set => Set(ref switchSummary, value); }
    public string Comparison { get => comparison; set => Set(ref comparison, value); }
    public string Gate { get => gate; set => Set(ref gate, value); }
    public event Action? SelectionChanged;
    public void Invalidate() { CanMigrate = false; Comparison = "选择已变化，请重新读取并比较"; Gate = "当前没有可写入的迁移预览"; }
}
