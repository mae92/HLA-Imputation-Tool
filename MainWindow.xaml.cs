
using ClosedXML.Excel;
using HLAImputation.Models;
using HLAImputation.Services;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace HLAImputation
{
    public partial class MainWindow : Window
    {
        private List<InputRecord>? _rawInputData;
        private List<InputRecord>? _effectiveInputData;
        private List<InputRecord>? _inputData;

        private readonly ReferenceStore _refStore;
        private readonly ImputationEngine _engine;
        private readonly DataCleaning _dataCleaning;
        private readonly GGroupConversionService _gGroupService;

        private SerologyToOneFieldMolService? _seroService;
        private List<InputRecord>? _imputationWorklist;

        private readonly Dictionary<string, string> _variantToBaseTx = new(StringComparer.OrdinalIgnoreCase);

        private QCService _qcService = new QCService();
        private List<InputRecord> _lastTransformedInputs = new();

        private CancellationTokenSource? _cts;

        private ObservableCollection<ImputedDisplay> _allResults = new();

        // mismatch maps keyed by "TxID|PropertyName"
        private readonly Dictionary<string, bool> _inputMismatchMap = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, bool> _resultMismatchMap = new(StringComparer.OrdinalIgnoreCase);

        // converters (must exist in your project)
        private readonly InputMismatchToBrushConverter _inputBrushConverter = new();
        private readonly ResultMismatchToBrushConverter _resultBrushConverter = new();

        // ✅ Run settings snapshot (used for Export "Run Settings" tab)
        private Dictionary<string, string> _lastRunSettings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // ✅ Lock because iterative approach temporarily overrides _engine.UseLocus per sample attempt
        private readonly object _engineLock = new object();

        public MainWindow()
        {
            InitializeComponent();

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string refCsvPath = System.IO.Path.Combine(baseDir, "Reference", "A-C-B-DRB-DQ-DP_Haplotypes_Dataframe.csv");
            string gGroupTablePath = System.IO.Path.Combine(baseDir, "Reference", "g-group_conversion_table.csv");

            if (!System.IO.File.Exists(gGroupTablePath))
            {
                MessageBox.Show("G-Group table not found:\n" + gGroupTablePath);
                return;
            }

            string serologyMapPath = System.IO.Path.Combine(baseDir, "Reference", "Antigen_to_OneFieldMol_Final_02192024.csv");
            _seroService = System.IO.File.Exists(serologyMapPath)
                ? new SerologyToOneFieldMolService(serologyMapPath)
                : null;

            string dbDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Imputation_App");

            string dbPath = System.IO.Path.Combine(dbDir, "hapref.sqlite");

            _refStore = new ReferenceStore(refCsvPath, dbPath);
            _engine = new ImputationEngine(_refStore);

            _gGroupService = new GGroupConversionService(gGroupTablePath);
            _dataCleaning = new DataCleaning(_gGroupService);

            _qcService.GGroupService = _gGroupService;

            InitSearchOrderDropdowns();

            // resolution change
            rbRaw.Checked += SettingsChanged_RefreshInputGrid;
            rbTwoField.Checked += SettingsChanged_RefreshInputGrid;
            rbOneField.Checked += SettingsChanged_RefreshInputGrid;

            // g-group toggle
            cbConvertToGGroup.Checked += SettingsChanged_RefreshAll;
            cbConvertToGGroup.Unchecked += SettingsChanged_RefreshAll;

            // filter
            cbShowOnlySuccessful.Checked += SettingsChanged_RefreshAll;
            cbShowOnlySuccessful.Unchecked += SettingsChanged_RefreshAll;

            // view toggles
            cbShowRawInput.Checked += SettingsChanged_RefreshAll;
            cbShowRawInput.Unchecked += SettingsChanged_RefreshAll;
            cbShowVariants.Checked += SettingsChanged_RefreshAll;
            cbShowVariants.Unchecked += SettingsChanged_RefreshAll;

            // input type toggle
            rbSerological.Checked += (s, e) => RebuildEffectiveInput();
            rbMolecular.Checked += (s, e) => RebuildEffectiveInput();
        }

        // ===========================================================
        // INPUT PREPROCESSING (RAW -> NORMALIZED -> OPTIONAL SERO MAP -> OPTIONAL EXPANSION)
        // ===========================================================
        private void RebuildEffectiveInput()
        {
            if (_rawInputData == null) return;

            _variantToBaseTx.Clear();

            var normalizedBase = _rawInputData.Select(AlleleInputNormalizer.NormalizeRecord).ToList();

            if (rbSerological.IsChecked == true && _seroService != null)
            {
                // one record per base sample for display/QC baseline
                _effectiveInputData = normalizedBase.Select(r => _seroService.ConvertRecord(r)).ToList();
                _inputData = _effectiveInputData;

                // expanded worklist for imputation
                var work = new List<InputRecord>();
                foreach (var r in normalizedBase)
                {
                    var variants = _seroService.ExpandRecordVariants(r, 50);
                    foreach (var v in variants)
                        _variantToBaseTx[v.TxID] = r.TxID;

                    work.AddRange(variants);
                }
                _imputationWorklist = work;
            }
            else
            {
                _effectiveInputData = normalizedBase;
                _inputData = _effectiveInputData;
                _imputationWorklist = _effectiveInputData;

                foreach (var r in _effectiveInputData)
                    _variantToBaseTx[r.TxID] = r.TxID;
            }

            RefreshInputGridDisplay();
            RefreshResultGridDisplay();
            UpdateMismatchMaps();
            UpdateQcReportForCurrentView();
        }

        // ===========================================================
        // DISPLAY: INPUT GRID (RAW vs VARIANTS vs PROCESSED)
        // ===========================================================
        private void RefreshInputGridDisplay()
        {
            if (_rawInputData == null) return;

            List<InputRecord>? sourceData;
            if (cbShowRawInput?.IsChecked == true)
                sourceData = _rawInputData;
            else if (cbShowVariants?.IsChecked == true)
                sourceData = _imputationWorklist;
            else
                sourceData = _inputData;

            if (sourceData == null) return;

            bool convertToGGroup = (cbConvertToGGroup?.IsChecked == true);
            string resolutionMode = GetResolutionMode();


            string ConvertForDisplay(string allele)
            {
                if (string.IsNullOrWhiteSpace(allele))
                    return "";

                // ✅ Raw mode → show untouched
                if (cbShowRawInput?.IsChecked == true || rbRaw.IsChecked == true)
                    return allele ?? "";

                string value = _dataCleaning.TransformAllele(allele, resolutionMode, convertToGGroup);

                // ✅ CLEAN DISPLAY ONLY FOR 2-FIELD MODE
                if (rbTwoField.IsChecked == true)
                {
                    if (value.EndsWith("G", StringComparison.OrdinalIgnoreCase) ||
                        value.EndsWith("P", StringComparison.OrdinalIgnoreCase))
                    {
                        value = value.Substring(0, value.Length - 1);
                    }
                }

                return value;
            }


            bool onlySuccess = (cbShowOnlySuccessful?.IsChecked == true);
            HashSet<string>? successSet = null;

            // Only filter by success if we have collapsed results (base TxIDs)
            if (onlySuccess && _allResults.Count > 0 && _allResults.Any(r => r.Success))
            {
                successSet = _allResults.Where(r => r.Success)
                                        .Select(r => r.TxID)
                                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                successSet = null; // do not filter if nothing succeeded
            }

            // helper to map variant IDs (Tx123.1) -> base IDs (Tx123)
            string BaseTx(string tx)
                => _variantToBaseTx.TryGetValue(tx, out var b) ? b : tx;

            var display = sourceData
                .Where(r => successSet == null || successSet.Contains(BaseTx(r.TxID)))
                .Select(x => new InputDisplay
                {
                    TxID = x.TxID,
                    Race = x.Race,
                    Type = x.PatType,
                    A1 = ConvertForDisplay(x.Loci["A"][0]),
                    A2 = ConvertForDisplay(x.Loci["A"][1]),
                    B1 = ConvertForDisplay(x.Loci["B"][0]),
                    B2 = ConvertForDisplay(x.Loci["B"][1]),
                    C1 = ConvertForDisplay(x.Loci["C"][0]),
                    C2 = ConvertForDisplay(x.Loci["C"][1]),
                    DRB11 = ConvertForDisplay(x.Loci["DRB1"][0]),
                    DRB12 = ConvertForDisplay(x.Loci["DRB1"][1]),
                    DRB3451 = x.Loci.ContainsKey("DRB345") ? ConvertForDisplay(x.Loci["DRB345"][0]) : "",
                    DRB3452 = x.Loci.ContainsKey("DRB345") ? ConvertForDisplay(x.Loci["DRB345"][1]) : "",
                    DQB11 = x.Loci.ContainsKey("DQB1") ? ConvertForDisplay(x.Loci["DQB1"][0]) : "",
                    DQB12 = x.Loci.ContainsKey("DQB1") ? ConvertForDisplay(x.Loci["DQB1"][1]) : "",
                    DQA11 = x.Loci.ContainsKey("DQA1") ? ConvertForDisplay(x.Loci["DQA1"][0]) : "",
                    DQA12 = x.Loci.ContainsKey("DQA1") ? ConvertForDisplay(x.Loci["DQA1"][1]) : "",
                    DPB11 = x.Loci.ContainsKey("DPB1") ? ConvertForDisplay(x.Loci["DPB1"][0]) : "",
                    DPB12 = x.Loci.ContainsKey("DPB1") ? ConvertForDisplay(x.Loci["DPB1"][1]) : "",
                    DPA11 = x.Loci.ContainsKey("DPA1") ? ConvertForDisplay(x.Loci["DPA1"][0]) : "",
                    DPA12 = x.Loci.ContainsKey("DPA1") ? ConvertForDisplay(x.Loci["DPA1"][1]) : ""
                })
                .ToList();

            InputGrid.ItemsSource = display;
        }

        private void RefreshResultGridDisplay()
        {
            bool onlySuccess =
                (cbShowOnlySuccessful?.IsChecked == true) &&
                _allResults != null &&
                _allResults.Any(r => r.Success);

            var view = onlySuccess
                ? _allResults.Where(r => r.Success).ToList()
                : _allResults.ToList();

            ResultGrid.ItemsSource = null;
            ResultGrid.ItemsSource = view;
        }

        private void SettingsChanged_RefreshAll(object? sender, RoutedEventArgs e)
        {
            RefreshInputGridDisplay();
            RefreshResultGridDisplay();
            UpdateMismatchMaps();
            UpdateQcReportForCurrentView();
        }

        private void SettingsChanged_RefreshInputGrid(object? sender, RoutedEventArgs e)
        {
            RefreshInputGridDisplay();
            UpdateMismatchMaps();
        }

        private string GetResolutionMode()
        {
            if (rbOneField.IsChecked == true) return "OneField";
            if (rbTwoField.IsChecked == true) return "TwoField";
            return "Raw";
        }

        // ===========================================================
        // INPUT LOAD
        // ===========================================================
        private void UploadCsv_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "CSV files (*.csv)|*.csv" };
            if (dlg.ShowDialog() != true) return;

            _rawInputData = CsvLoader.LoadInput(dlg.FileName);

            RebuildEffectiveInput();

            ImputeStatusText.Text = $"Loaded {_rawInputData.Count} rows";
            ImputeProgressBar.Value = 0;
            ImputePercentText.Text = "0%";
        }

        // ===========================================================
        // IMPUTATION RUN (variants -> collapse by highest FreqDip)
        // ===========================================================
        private async void Run_Click(object sender, RoutedEventArgs e)
        {
            if (_inputData == null || _inputData.Count == 0)
            {
                MessageBox.Show("Load an input CSV first.");
                return;
            }

            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            ApplyGuiSettingsToEngine();

            // ===========================================================
            // ✅ FIX 1: Make sure the ResultGrid is bound to the live collection during run
            // (Your old code only displayed results after the run finished.)
            // ===========================================================
            Dispatcher.Invoke(() =>
            {
                _allResults.Clear();
                ResultGrid.ItemsSource = _allResults;

                ImputeStatusText.Text = "Imputing...";
                ImputeProgressBar.Value = 0;
                ImputePercentText.Text = "0%";
            });

            var worklist = _imputationWorklist ?? _inputData;

            var candidates = new List<(string BaseTx, InputRecord TransformedInput, ImputedDisplay Display)>();

            int total = worklist.Count;
            int done = 0;
            int found = 0;

            bool convertToGGroup = (cbConvertToGGroup?.IsChecked == true);
            string resolutionMode = GetResolutionMode();
            var baseUseLocus = GetUseLocusMap();
            bool iterativeEnabled = (cbUseIterativeApproach?.IsChecked == true);

            // ✅ Cache run settings for Export "Run Settings"
            _lastRunSettings = BuildRunSettingsSnapshot(baseUseLocus, iterativeEnabled, convertToGGroup, resolutionMode, total);

            // ===========================================================
            // ✅ FIX 2 + FIX 3:
            // - More frequent progress updates (every 25 by default)
            // - Stream results to UI using a buffered flush (avoids UI freeze)
            // ===========================================================
            const int UI_FLUSH_BATCH = 25;        // how many rows to batch before pushing to UI
            const int PROGRESS_BATCH = 25;        // how often to update progress text/bar
            var uiBuffer = new List<ImputedDisplay>(UI_FLUSH_BATCH);

            await Task.Run(() =>
            {
                foreach (var rawInput in worklist)
                {
                    token.ThrowIfCancellationRequested();

                    // Compute per-sample iterative (or non-iterative) result
                    var (res, transformedInput, usedMap, failureReason, raceStrategyUsed) =
    ImputeOneWithOptionalIterativeFallback(rawInput, resolutionMode, convertToGGroup, baseUseLocus, iterativeEnabled);

                    done++;
                    bool success = res != null;
                    if (success) found++;

                    string baseTx = _variantToBaseTx.TryGetValue(rawInput.TxID, out var b) ? b : rawInput.TxID;
                    string genesUsed = BuildGenesUsedString(usedMap);

                    var d = new ImputedDisplay
                    {
                        TxID = rawInput.TxID, // variant ID for now; collapsed later to base
                        Race = transformedInput.Race,
                        Type = transformedInput.PatType,

                        GenesUsed = genesUsed,

                        A1 = success ? res.H1.Alleles["A"] : "",
                        A2 = success ? res.H2.Alleles["A"] : "",
                        B1 = success ? res.H1.Alleles["B"] : "",
                        B2 = success ? res.H2.Alleles["B"] : "",
                        C1 = success ? res.H1.Alleles["C"] : "",
                        C2 = success ? res.H2.Alleles["C"] : "",
                        DRB11 = success ? res.H1.Alleles["DRB1"] : "",
                        DRB12 = success ? res.H2.Alleles["DRB1"] : "",
                        DRB3451 = success ? res.H1.Alleles["DRB345"] : "",
                        DRB3452 = success ? res.H2.Alleles["DRB345"] : "",
                        DQB11 = success ? res.H1.Alleles["DQB1"] : "",
                        DQB12 = success ? res.H2.Alleles["DQB1"] : "",
                        DQA11 = success ? res.H1.Alleles["DQA1"] : "",
                        DQA12 = success ? res.H2.Alleles["DQA1"] : "",
                        DPB11 = success ? res.H1.Alleles["DPB1"] : "",
                        DPB12 = success ? res.H2.Alleles["DPB1"] : "",
                        DPA11 = success ? res.H1.Alleles["DPA1"] : "",
                        DPA12 = success ? res.H2.Alleles["DPA1"] : "",

                        FreqH1 = success ? res.FreqH1 : 0,
                        FreqH2 = success ? res.FreqH2 : 0,
                        FreqDip = success ? res.FreqDip : 0,
                        Mismatch = success ? res.MismatchCount : 0,
                        Selection = success ? res.FinalSelection : "FAILED",

                        RaceStrategyUsed = success ? (res.RaceStrategyUsed ?? raceStrategyUsed) : raceStrategyUsed,
                        FailureReason = success ? "" : failureReason,

                        Success = success
                    };

                    candidates.Add((baseTx, transformedInput, d));

                    // ✅ Stream into UI buffer
                    uiBuffer.Add(d);

                    // Flush buffer to UI every N rows (or at the very end)
                    bool shouldFlush = (uiBuffer.Count >= UI_FLUSH_BATCH) || (done == total);

                    // ✅ Progress update more frequently (every PROGRESS_BATCH rows)
                    bool shouldProgress = (done == 1) || (done == total) || (done % PROGRESS_BATCH == 0);

                    if (shouldFlush || shouldProgress)
                    {
                        // Snapshot values so closures are safe
                        var flushList = shouldFlush ? uiBuffer.ToList() : null;
                        if (shouldFlush) uiBuffer.Clear();

                        int doneSnap = done;
                        int totalSnap = total;
                        int foundSnap = found;

                        string txSnap = rawInput.TxID;
                        string genesSnap = genesUsed;

                        // Use BeginInvoke so the background thread does not block on the UI thread
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            // ✅ Fix 1: actually add results to the bound ObservableCollection
                            if (flushList != null)
                            {
                                foreach (var item in flushList)
                                    _allResults.Add(item);
                            }

                            if (shouldProgress)
                            {
                                int pct = (int)Math.Round(doneSnap * 100.0 / totalSnap);
                                ImputeProgressBar.Value = pct;
                                ImputePercentText.Text = pct + "%";

                                // ✅ Bonus: show current sample + genes used for debugging iterative mode
                                ImputeStatusText.Text =
                                    $"Imputing... {doneSnap}/{totalSnap} | Found: {foundSnap} | Last: {txSnap} | GenesUsed: {genesSnap}";
                            }
                        }), System.Windows.Threading.DispatcherPriority.Background);
                    }
                }
            }, token);

            // collapse to base TxID
            _allResults.Clear();
            var chosenTransformedByBase = new Dictionary<string, InputRecord>(StringComparer.OrdinalIgnoreCase);

            foreach (var grp in candidates.GroupBy(c => c.BaseTx, StringComparer.OrdinalIgnoreCase))
            {
                var best = grp
                    .OrderByDescending(x => x.Display.Success)
                    .ThenByDescending(x => x.Display.FreqDip)
                    .First();

                // Set collapsed TxID to base
                best.Display.TxID = grp.Key;

                _allResults.Add(best.Display);
                chosenTransformedByBase[grp.Key] = best.TransformedInput;
            }

            // align transformed inputs to base list for QC (kept as you had it)
            _lastTransformedInputs = _inputData
                .Select(r =>
                {
                    if (chosenTransformedByBase.TryGetValue(r.TxID, out var t))
                        return t;

                    return _dataCleaning.TransformRecord(r, resolutionMode, convertToGGroup, baseUseLocus);
                })
                .ToList();

            UpdateQcReportForCurrentView();
            RefreshResultGridDisplay();
            RefreshInputGridDisplay();
            UpdateMismatchMaps();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
        }

        // ===========================================================
        // ENGINE SETTINGS (safe lookup for optional controls)
        // ===========================================================
        private void ApplyGuiSettingsToEngine()
        {
            if (rbOneField.IsChecked == true) _engine.InputResolutionMode = "OneField";
            else if (rbTwoField.IsChecked == true) _engine.InputResolutionMode = "TwoField";
            else _engine.InputResolutionMode = "Raw";

            // Safe: may not exist depending on XAML version
            var rbMustMatch = FindName("rbMustMatch") as RadioButton;
            _engine.MustMatchInput = (rbMustMatch?.IsChecked == true);

            var tbTopHaps = FindName("tbTopHaps") as TextBox;
            if (tbTopHaps != null && int.TryParse(tbTopHaps.Text.Trim(), out int topN) && topN > 0)
                _engine.MaxHaplotypes = topN;
            else
                _engine.MaxHaplotypes = 1000000;

            _engine.UseLocus["A"] = cbA.IsChecked == true;
            _engine.UseLocus["B"] = cbB.IsChecked == true;
            _engine.UseLocus["C"] = cbC.IsChecked == true;
            _engine.UseLocus["DRB1"] = cbDRB1.IsChecked == true;
            _engine.UseLocus["DRB345"] = cbDRB345.IsChecked == true;
            _engine.UseLocus["DQB1"] = cbDQB1.IsChecked == true;
            _engine.UseLocus["DQA1"] = cbDQA1.IsChecked == true;
            _engine.UseLocus["DPB1"] = cbDPB1.IsChecked == true;
            _engine.UseLocus["DPA1"] = cbDPA1.IsChecked == true;

            var orderMap = new Dictionary<string, int>
            {
                { "A", (int)(ddA.SelectedItem ?? 1) },
                { "B", (int)(ddB.SelectedItem ?? 2) },
                { "C", (int)(ddC.SelectedItem ?? 5) },
                { "DRB1", (int)(ddDRB1.SelectedItem ?? 3) },
                { "DRB345", (int)(ddDRB345.SelectedItem ?? 7) },
                { "DQB1", (int)(ddDQB1.SelectedItem ?? 4) },
                { "DQA1", (int)(ddDQA1.SelectedItem ?? 6) },
                { "DPB1", (int)(ddDPB1.SelectedItem ?? 8) },
                { "DPA1", (int)(ddDPA1.SelectedItem ?? 9) }
            };

            _engine.SearchOrder = orderMap
                .OrderBy(kv => kv.Value)
                .Select(kv => kv.Key)
                .ToList();
        }

        private Dictionary<string, bool> GetUseLocusMap()
        {
            return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                { "A", cbA.IsChecked == true },
                { "B", cbB.IsChecked == true },
                { "C", cbC.IsChecked == true },
                { "DRB1", cbDRB1.IsChecked == true },
                { "DRB345", cbDRB345.IsChecked == true },
                { "DQB1", cbDQB1.IsChecked == true },
                { "DQA1", cbDQA1.IsChecked == true },
                { "DPB1", cbDPB1.IsChecked == true },
                { "DPA1", cbDPA1.IsChecked == true }
            };
        }

        // ===========================================================
        // ✅ ITERATIVE IMPUTATION HELPERS
        // ===========================================================
        // Build a per-sample use map that only includes loci that are enabled in UI AND have any data for that sample.
        private Dictionary<string, bool> BuildPerSampleUseLocusMap(InputRecord rec, Dictionary<string, bool> baseUseLocus)
        {
            var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            foreach (var kv in baseUseLocus)
            {
                string locus = kv.Key;
                bool enabled = kv.Value;

                bool hasData = false;
                if (rec != null && rec.Loci != null && rec.Loci.TryGetValue(locus, out var arr) && arr != null)
                {
                    for (int i = 0; i < arr.Length; i++)
                    {
                        if (!string.IsNullOrWhiteSpace(arr[i]))
                        {
                            hasData = true;
                            break;
                        }
                    }
                }

                map[locus] = enabled && hasData;
            }

            return map;
        }

        // Convert a use map to a consistent string, ordered by the engine's current SearchOrder.
        private string BuildGenesUsedString(Dictionary<string, bool> useMap)
        {
            if (useMap == null) return "";

            var order = _engine?.SearchOrder ?? new List<string>();
            var used = new List<string>();

            // Add in search order first
            foreach (var locus in order)
            {
                if (useMap.TryGetValue(locus, out bool on) && on)
                    used.Add(locus);
            }

            // Add any remaining keys not in SearchOrder (just in case)
            foreach (var kv in useMap)
            {
                if (kv.Value && !used.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))
                    used.Add(kv.Key);
            }

            return string.Join(",", used);
        }

        // Run ProcessSingle with a temporary UseLocus override.
        // This is necessary because ImputationEngine uses its internal UseLocus during stepwise filtering.
        private DiplotypeResult? ProcessSingleWithUseLocusOverride(
    InputRecord transformedInput,
    Dictionary<string, bool> attemptUseMap,
    out string failureReason,
    out string raceStrategyUsed)
        {
            lock (_engineLock)
            {
                var saved = _engine.UseLocus;
                try
                {
                    _engine.UseLocus = new Dictionary<string, bool>(attemptUseMap, StringComparer.OrdinalIgnoreCase);
                    return _engine.ProcessSingle(transformedInput, out failureReason, out raceStrategyUsed);
                }
                finally
                {
                    _engine.UseLocus = saved;
                }
            }
        }

        // One sample: either run once (non-iterative) or iteratively drop loci from the END of SearchOrder until success.
        private (DiplotypeResult? Result,
         InputRecord TransformedInput,
         Dictionary<string, bool> UsedMap,
         string FailureReason,
         string RaceStrategyUsed)
    ImputeOneWithOptionalIterativeFallback(
        InputRecord rawInput,
        string resolutionMode,
        bool convertToGGroup,
        Dictionary<string, bool> baseUseLocus,
        bool iterativeEnabled)
        {
            var currentUse = BuildPerSampleUseLocusMap(rawInput, baseUseLocus);
            InputRecord transformed = _dataCleaning.TransformRecord(rawInput, resolutionMode, convertToGGroup, currentUse);

            string failureReason;
            string raceStrategyUsed;

            if (!iterativeEnabled)
            {
                var resOnce = ProcessSingleWithUseLocusOverride(transformed, currentUse, out failureReason, out raceStrategyUsed);
                return (resOnce, transformed, currentUse, failureReason, raceStrategyUsed);
            }

            var res = ProcessSingleWithUseLocusOverride(transformed, currentUse, out failureReason, out raceStrategyUsed);
            if (res != null)
                return (res, transformed, currentUse, failureReason, raceStrategyUsed);

            string lastFailureReason = failureReason;
            string lastRaceStrategy = raceStrategyUsed;

            var order = _engine.SearchOrder.ToList();
            const int MIN_SEARCH_ORDER_POSITIONS_TO_KEEP = 3;

            for (int idx = order.Count - 1; idx >= MIN_SEARCH_ORDER_POSITIONS_TO_KEEP; idx--)
            {
                string locusToDrop = order[idx];

                if (currentUse.TryGetValue(locusToDrop, out bool on) && on)
                {
                    currentUse[locusToDrop] = false;
                    transformed = _dataCleaning.TransformRecord(rawInput, resolutionMode, convertToGGroup, currentUse);

                    res = ProcessSingleWithUseLocusOverride(transformed, currentUse, out failureReason, out raceStrategyUsed);

                    lastFailureReason = failureReason;
                    lastRaceStrategy = raceStrategyUsed;

                    if (res != null)
                        return (res, transformed, currentUse, failureReason, raceStrategyUsed);
                }
            }

            string summary =
                (string.IsNullOrWhiteSpace(lastFailureReason) ? "Imputation failed." : lastFailureReason)
                + " Iterative fallback removed loci down to the top 3 search-order positions without success.";

            return (null, transformed, currentUse, summary, lastRaceStrategy);
        }

        // Snapshot settings used for the run (written into Export "Run Settings" tab)
        private Dictionary<string, string> BuildRunSettingsSnapshot(
            Dictionary<string, bool> baseUseLocus,
            bool iterativeEnabled,
            bool convertToGGroup,
            string resolutionMode,
            int worklistCount)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            d["RunTimestamp"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            d["InputType"] = (rbSerological.IsChecked == true) ? "Serological" : "Molecular";
            d["ResolutionMode"] = resolutionMode;
            d["ConvertToGGroup"] = convertToGGroup.ToString();
            d["UseIterativeApproach"] = iterativeEnabled.ToString();
            d["MustMatchInput"] = _engine.MustMatchInput.ToString();
            d["MaxHaplotypes"] = _engine.MaxHaplotypes.ToString();
            d["SearchOrder"] = string.Join(" > ", _engine.SearchOrder);
            d["LociEnabled_UI"] = string.Join(",", baseUseLocus.Where(kv => kv.Value).Select(kv => kv.Key));
            d["WorklistCount"] = worklistCount.ToString();

            // If the textbox exists, record it too
            var tbTopHaps = FindName("tbTopHaps") as TextBox;
            d["tbTopHaps_TextBoxValue"] = tbTopHaps?.Text ?? "(not present)";

            return d;
        }

        private void InitSearchOrderDropdowns()
        {
            var nums = Enumerable.Range(1, 9).ToList();
            ddA.ItemsSource = nums;
            ddB.ItemsSource = nums;
            ddC.ItemsSource = nums;
            ddDRB1.ItemsSource = nums;
            ddDRB345.ItemsSource = nums;
            ddDQB1.ItemsSource = nums;
            ddDQA1.ItemsSource = nums;
            ddDPB1.ItemsSource = nums;
            ddDPA1.ItemsSource = nums;

            ddA.SelectedItem = 1;
            ddB.SelectedItem = 2;
            ddC.SelectedItem = 5;
            ddDRB1.SelectedItem = 3;
            ddDRB345.SelectedItem = 7;
            ddDQB1.SelectedItem = 4;
            ddDQA1.SelectedItem = 6;
            ddDPB1.SelectedItem = 8;
            ddDPA1.SelectedItem = 9;
        }

        // ===========================================================
        // QC
        // ===========================================================
        private void UpdateQcReportForCurrentView()
        {
            if (_inputData == null || _inputData.Count == 0) return;
            if (_allResults == null || _allResults.Count == 0) return;

            bool onlySuccess = (cbShowOnlySuccessful?.IsChecked == true);

            int n = Math.Min(_inputData.Count, _allResults.Count);
            if (_lastTransformedInputs != null && _lastTransformedInputs.Count > 0)
                n = Math.Min(n, _lastTransformedInputs.Count);

            var idx = Enumerable.Range(0, n)
                .Where(i => !onlySuccess || _allResults[i].Success)
                .ToList();

            var rawSubset = idx.Select(i => _inputData[i]).ToList();

            var transformedSubset = (_lastTransformedInputs != null && _lastTransformedInputs.Count >= n)
                ? idx.Select(i => _lastTransformedInputs[i]).ToList()
                : new List<InputRecord>();

            var resultsSubset = idx.Select(i => _allResults[i]).ToList();

            string qc = _qcService.GenerateQCReport(
                rawInput: rawSubset,
                transformedInput: transformedSubset,
                results: resultsSubset,
                resolutionMode: GetResolutionMode()
            );

            Dispatcher.Invoke(() => { QcReportTextBox.Text = qc; });
        }

        // ===========================================================
        // MISMATCH MAP COMPUTATION + RED HIGHLIGHT SUPPORT (SINGLE COPY ONLY)
        // ===========================================================
        public bool IsInputMismatch(string txid, string prop)
            => _inputMismatchMap.TryGetValue($"{txid}|{prop}", out var m) && m;

        public bool IsResultMismatch(string txid, string prop)
            => _resultMismatchMap.TryGetValue($"{txid}|{prop}", out var m) && m;

        private void InputGrid_AutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
        {
            if (!IsAlleleProperty(e.PropertyName)) return;
            e.Column.CellStyle = BuildCellStyleForColumn(true, e.PropertyName);
        }

        private void ResultGrid_AutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
        {
            if (!IsAlleleProperty(e.PropertyName)) return;
            e.Column.CellStyle = BuildCellStyleForColumn(false, e.PropertyName);
        }

        private Style BuildCellStyleForColumn(bool isInput, string propertyName)
        {
            var style = new Style(typeof(DataGridCell));
            style.Setters.Add(new Setter(DataGridCell.ForegroundProperty, Brushes.Black));

            var binding = new Binding("TxID")
            {
                Converter = isInput ? (IValueConverter)_inputBrushConverter : (IValueConverter)_resultBrushConverter,
                ConverterParameter = propertyName
            };

            style.Setters.Add(new Setter(DataGridCell.ForegroundProperty, binding));
            return style;
        }

        private bool IsAlleleProperty(string prop)
        {
            return prop is
                "A1" or "A2" or
                "B1" or "B2" or
                "C1" or "C2" or
                "DRB11" or "DRB12" or
                "DRB3451" or "DRB3452" or
                "DQB11" or "DQB12" or
                "DQA11" or "DQA12" or
                "DPB11" or "DPB12" or
                "DPA11" or "DPA12";
        }

        private void UpdateMismatchMaps()
        {
            _inputMismatchMap.Clear();
            _resultMismatchMap.Clear();

            if (InputGrid.ItemsSource == null || ResultGrid.ItemsSource == null)
                return;

            var inputRows = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in (System.Collections.IEnumerable)InputGrid.ItemsSource)
            {
                var tx = row?.GetType().GetProperty("TxID")?.GetValue(row)?.ToString();
                if (!string.IsNullOrWhiteSpace(tx) && !inputRows.ContainsKey(tx))
                    inputRows[tx] = row!;
            }

            var resultRows = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in (System.Collections.IEnumerable)ResultGrid.ItemsSource)
            {
                var tx = row?.GetType().GetProperty("TxID")?.GetValue(row)?.ToString();
                if (!string.IsNullOrWhiteSpace(tx) && !resultRows.ContainsKey(tx))
                    resultRows[tx] = row!;
            }

            foreach (var txid in inputRows.Keys.Intersect(resultRows.Keys, StringComparer.OrdinalIgnoreCase))
            {
                var inRow = inputRows[txid];
                var outRow = resultRows[txid];

                MarkLocus(txid, inRow, outRow, "A1", "A2");
                MarkLocus(txid, inRow, outRow, "B1", "B2");
                MarkLocus(txid, inRow, outRow, "C1", "C2");
                MarkLocus(txid, inRow, outRow, "DRB11", "DRB12");
                MarkLocus(txid, inRow, outRow, "DRB3451", "DRB3452");
                MarkLocus(txid, inRow, outRow, "DQB11", "DQB12");
                MarkLocus(txid, inRow, outRow, "DQA11", "DQA12");
                MarkLocus(txid, inRow, outRow, "DPB11", "DPB12");
                MarkLocus(txid, inRow, outRow, "DPA11", "DPA12");
            }

            InputGrid.Items.Refresh();
            ResultGrid.Items.Refresh();
        }

        private void MarkLocus(string txid, object inRow, object outRow, string p1, string p2)
        {
            string in1 = NormalizeForHighlight(GetProp(inRow, p1));
            string in2 = NormalizeForHighlight(GetProp(inRow, p2));
            string out1 = NormalizeForHighlight(GetProp(outRow, p1));
            string out2 = NormalizeForHighlight(GetProp(outRow, p2));

            var outPool = new List<string>(new[] { out1, out2 }.Where(s => !string.IsNullOrWhiteSpace(s)));
            bool in1Mismatch = !TryConsume(outPool, in1);
            bool in2Mismatch = !TryConsume(outPool, in2);

            var inPool = new List<string>(new[] { in1, in2 }.Where(s => !string.IsNullOrWhiteSpace(s)));
            bool out1Mismatch = !TryConsume(inPool, out1);
            bool out2Mismatch = !TryConsume(inPool, out2);

            _inputMismatchMap[$"{txid}|{p1}"] = (!string.IsNullOrWhiteSpace(in1)) && in1Mismatch;
            _inputMismatchMap[$"{txid}|{p2}"] = (!string.IsNullOrWhiteSpace(in2)) && in2Mismatch;

            _resultMismatchMap[$"{txid}|{p1}"] = (!string.IsNullOrWhiteSpace(out1)) && out1Mismatch;
            _resultMismatchMap[$"{txid}|{p2}"] = (!string.IsNullOrWhiteSpace(out2)) && out2Mismatch;
        }

        private bool TryConsume(List<string> pool, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return true;

            int idx = pool.FindIndex(x => x.Equals(value, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
            {
                pool.RemoveAt(idx);
                return true;
            }

            return false;
        }

        private string GetProp(object obj, string propName)
            => obj.GetType().GetProperty(propName)?.GetValue(obj)?.ToString() ?? "";




        private string NormalizeForHighlight(string allele)
        {
            if (string.IsNullOrWhiteSpace(allele))
                return "";

            allele = allele.Trim();

            // ✅ Only clean suffixes in 2-field mode
            if (rbTwoField.IsChecked == true)
            {
                if (allele.EndsWith("G", StringComparison.OrdinalIgnoreCase) ||
                    allele.EndsWith("P", StringComparison.OrdinalIgnoreCase))
                {
                    allele = allele.Substring(0, allele.Length - 1);
                }

                return AlleleUtils.ToTwoField(allele);
            }

            // ✅ 1-field mode (already clean)
            if (rbOneField.IsChecked == true)
            {
                return AlleleUtils.ToOneField(allele);
            }

            // ✅ User provided mode → leave raw
            return allele;
        }




        // ===========================================================
        // DOUBLE CLICK SYNC
        // ===========================================================
        private void InputGrid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (InputGrid.CurrentItem == null) return;

            int index = InputGrid.Items.IndexOf(InputGrid.CurrentItem);
            if (index < 0 || index >= ResultGrid.Items.Count) return;

            ResultGrid.SelectedItem = ResultGrid.Items[index];
            ResultGrid.ScrollIntoView(ResultGrid.SelectedItem);
        }

        private void ResultGrid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (ResultGrid.CurrentItem == null) return;

            int index = ResultGrid.Items.IndexOf(ResultGrid.CurrentItem);
            if (index < 0 || index >= InputGrid.Items.Count) return;

            InputGrid.SelectedItem = InputGrid.Items[index];
            InputGrid.ScrollIntoView(InputGrid.SelectedItem);
        }

        // ===========================================================
        // EXPORT (simple, compiling export so your squiggles go away)
        // ===========================================================
        private void Export_Click(object sender, RoutedEventArgs e)
        {
            if (_rawInputData == null || _rawInputData.Count == 0)
            {
                MessageBox.Show("No input loaded to export.");
                return;
            }

            var dlg = new SaveFileDialog
            {
                Filter = "Excel Workbook (*.xlsx)|*.xlsx",
                FileName = $"Imputation_Export_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx"
            };

            if (dlg.ShowDialog() != true) return;

            using var wb = new XLWorkbook();

            // ===========================================================
            // Sheet 1: Raw Input (FULL alleles)
            // ===========================================================
            var wsRaw = wb.Worksheets.Add("Raw Input");

            var rawTable = _rawInputData.Select(r => new
            {
                r.TxID,
                r.Race,
                r.PatType,
                A1 = r.Loci.ContainsKey("A") ? r.Loci["A"][0] : "",
                A2 = r.Loci.ContainsKey("A") ? r.Loci["A"][1] : "",
                B1 = r.Loci.ContainsKey("B") ? r.Loci["B"][0] : "",
                B2 = r.Loci.ContainsKey("B") ? r.Loci["B"][1] : "",
                C1 = r.Loci.ContainsKey("C") ? r.Loci["C"][0] : "",
                C2 = r.Loci.ContainsKey("C") ? r.Loci["C"][1] : "",
                DRB11 = r.Loci.ContainsKey("DRB1") ? r.Loci["DRB1"][0] : "",
                DRB12 = r.Loci.ContainsKey("DRB1") ? r.Loci["DRB1"][1] : "",
                DRB3451 = r.Loci.ContainsKey("DRB345") ? r.Loci["DRB345"][0] : "",
                DRB3452 = r.Loci.ContainsKey("DRB345") ? r.Loci["DRB345"][1] : "",
                DQB11 = r.Loci.ContainsKey("DQB1") ? r.Loci["DQB1"][0] : "",
                DQB12 = r.Loci.ContainsKey("DQB1") ? r.Loci["DQB1"][1] : "",
                DQA11 = r.Loci.ContainsKey("DQA1") ? r.Loci["DQA1"][0] : "",
                DQA12 = r.Loci.ContainsKey("DQA1") ? r.Loci["DQA1"][1] : "",
                DPB11 = r.Loci.ContainsKey("DPB1") ? r.Loci["DPB1"][0] : "",
                DPB12 = r.Loci.ContainsKey("DPB1") ? r.Loci["DPB1"][1] : "",
                DPA11 = r.Loci.ContainsKey("DPA1") ? r.Loci["DPA1"][0] : "",
                DPA12 = r.Loci.ContainsKey("DPA1") ? r.Loci["DPA1"][1] : ""
            }).ToList();

            wsRaw.Cell(1, 1).InsertTable(rawTable);
            wsRaw.Columns().AdjustToContents();

            // ===========================================================
            // Sheet 2: Transformed Input (as displayed)
            // ===========================================================
            var wsInput = wb.Worksheets.Add("Transformed Input");
            var inputItems = InputGrid.ItemsSource as System.Collections.IEnumerable;

            if (inputItems != null)
            {
                var inputList = inputItems.Cast<object>().ToList();
                wsInput.Cell(1, 1).InsertTable(inputList);
                wsInput.Columns().AdjustToContents();
            }

            // ===========================================================
            // Sheet 3: Imputed Output (includes GenesUsed)
            // ===========================================================
            var wsOut = wb.Worksheets.Add("Imputed Output");
            wsOut.Cell(1, 1).InsertTable(_allResults);
            wsOut.Columns().AdjustToContents();

            // ===========================================================
            // Sheet 4: Mismatch Review (red allele text)
            // ===========================================================
            var wsMismatch = wb.Worksheets.Add("Mismatch Review");
            wsMismatch.Cell(1, 1).InsertTable(_allResults);

            // Identify column headers and apply red formatting for allele mismatches
            int lastCol = wsMismatch.LastColumnUsed().ColumnNumber();
            int lastRow = (_allResults?.Count ?? 0) + 1;

            for (int row = 2; row <= lastRow; row++)
            {
                string txid = wsMismatch.Cell(row, 1).GetString();

                for (int col = 1; col <= lastCol; col++)
                {
                    string propName = wsMismatch.Cell(1, col).GetString();

                    if (IsAlleleProperty(propName))
                    {
                        if (IsResultMismatch(txid, propName))
                        {
                            var cell = wsMismatch.Cell(row, col);
                            cell.Style.Font.FontColor = XLColor.Red;
                            cell.Style.Font.Bold = true;
                        }
                    }
                }
            }

            wsMismatch.Columns().AdjustToContents();

            // ===========================================================
            // Sheet 5: QC Report
            // ===========================================================
            var wsQc = wb.Worksheets.Add("QC Report");
            var qcText = QcReportTextBox.Text ?? "";
            var lines = qcText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

            for (int i = 0; i < lines.Length; i++)
                wsQc.Cell(i + 1, 1).Value = lines[i];

            wsQc.Column(1).Width = 130;
            wsQc.Column(1).Style.Font.FontName = "Consolas";
            wsQc.Column(1).Style.Font.FontSize = 11;

            // ===========================================================
            // Sheet 6: Run Settings (NEW)
            // ===========================================================
            var wsSettings = wb.Worksheets.Add("Run Settings");
            wsSettings.Cell(1, 1).Value = "Setting";
            wsSettings.Cell(1, 2).Value = "Value";
            wsSettings.Row(1).Style.Font.Bold = true;

            // If no run has occurred yet, build a live snapshot
            var settings = (_lastRunSettings != null && _lastRunSettings.Count > 0)
                ? _lastRunSettings
                : BuildRunSettingsSnapshot(GetUseLocusMap(),
                                           (cbUseIterativeApproach?.IsChecked == true),
                                           (cbConvertToGGroup?.IsChecked == true),
                                           GetResolutionMode(),
                                           (_imputationWorklist ?? _inputData ?? new List<InputRecord>()).Count);

            int r = 2;
            foreach (var kv in settings)
            {
                wsSettings.Cell(r, 1).Value = kv.Key;
                wsSettings.Cell(r, 2).Value = kv.Value;
                r++;
            }

            wsSettings.Columns().AdjustToContents();

            // ===========================================================
            // SAVE
            // ===========================================================
            wb.SaveAs(dlg.FileName);
            MessageBox.Show("Export complete.");
        }
    }
}
