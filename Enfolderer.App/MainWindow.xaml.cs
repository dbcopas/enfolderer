using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging; // may still be used indirectly for image handling
using Enfolderer.App.Imaging;
using Enfolderer.App.Infrastructure;
using Enfolderer.App.Importing; // import service usage
using Enfolderer.App.Collection;
using Enfolderer.App.Quantity;
using Enfolderer.App.Layout;
using Enfolderer.App.Binder;
using Enfolderer.App.Metadata; // orchestrator & provider types
using Enfolderer.App.Core;
using Enfolderer.App.Core.Abstractions;
using Enfolderer.App.Utilities;
using Enfolderer.App.Lands;

namespace Enfolderer.App;

public partial class MainWindow : Window
{
    private readonly BinderViewModel _vm;
    private void SearchFocus_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        try
        {
            var tb = this.FindName("SearchNameBox") as System.Windows.Controls.TextBox;
            if (tb != null)
            {
                tb.Focus();
                tb.SelectAll();
            }
        }
        catch { }
    }

    private async void UpdateMainDbFromCsv_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select CSV File to Update mainDb.db",
                Filter = "CSV Files (*.csv)|*.csv|All Files (*.*)|*.*"
            };
            if (dlg.ShowDialog(this) != true) return;

            // If Ctrl is held, run legacy updater; otherwise, auto-detect format
            bool useLegacy = (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl));
            if (useLegacy)
            {
                _vm.StartImportProgress("Legacy CSV update");
                var result = await Task.Run(() => CsvMainDbUpdater.Process(dlg.FileName, progress: (done, total) => _vm.ReportImportProgress(done, total)));
                _vm.FinishImportProgress();
                MessageBox.Show(this, $"Legacy update complete:\nUpdated: {result.Updated}\nInserted: {result.Inserted}\nErrors: {result.Errors}", "CSV Utility", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Auto-detect MTG Studio CSV format (comma-delimited with CardId header)
            bool isMtgsStudio = CsvMainDbUpdater.IsMtgsStudioCsvFormat(dlg.FileName);
            if (isMtgsStudio)
            {
                // MTG Studio CSV: prepare plan (matches only, no writes)
                _vm.StartImportProgress("Studio CSV matching");
                StudioCsvPlan? studioPlan = null;
                var studioDry = await Task.Run(() =>
                {
                    var (result, plan) = CsvMainDbUpdater.PrepareStudioCsvPlan(dlg.FileName, progress: (done, total) => _vm.ReportImportProgress(done, total));
                    studioPlan = plan;
                    return result;
                });
                _vm.FinishImportProgress();
                var studioSb = new System.Text.StringBuilder();
                studioSb.AppendLine("MTG Studio CSV plan:");
                studioSb.AppendLine($"Will update MtgsId on: {studioDry.UpdatedMtgsIds}");
                studioSb.AppendLine($"Already mapped (skipped): {studioDry.SkippedExisting}");
                studioSb.AppendLine($"Conflicts: {studioDry.Conflicts}");
                studioSb.AppendLine($"Unmatched: {(studioDry.UnmatchedLogPath != null ? "see log " + studioDry.UnmatchedLogPath : "0")} ");
                studioSb.AppendLine($"Errors: {studioDry.Errors}");
                studioSb.AppendLine();
                studioSb.AppendLine("Proceed to apply updates?\nClick Yes to apply only updates; No to also insert unmatched; Cancel to abort.");
                var studioChoice = MessageBox.Show(this, studioSb.ToString(), "Studio CSV Mapping", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (studioChoice == MessageBoxResult.Cancel) return;

                bool studioInsertMissing = (studioChoice == MessageBoxResult.No);
                _vm.StartImportProgress(studioInsertMissing ? "Studio CSV apply + insert" : "Studio CSV apply");
                var studioApply = await Task.Run(() => CsvMainDbUpdater.ApplyStudioCsvPlan(studioPlan!, insertMissing: studioInsertMissing, progress: (done, total) => _vm.ReportImportProgress(done, total)));
                _vm.FinishImportProgress();
                MessageBox.Show(this, $"Studio CSV mapping applied:\nUpdated MtgsId: {studioApply.UpdatedMtgsIds}\nInserted new: {studioApply.InsertedNew}\nSkipped existing: {studioApply.SkippedExisting}\nConflicts: {studioApply.Conflicts}\nErrors: {studioApply.Errors}\nUnmatched log: {(studioApply.UnmatchedLogPath ?? "(none)")}", "Studio CSV", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // MTGS mapping dry-run (semicolon format fallback)
            _vm.StartImportProgress("MTGS dry-run");
            var dry = await Task.Run(() => CsvMainDbUpdater.ProcessMtgsMapping(dlg.FileName, dryRun: true, insertMissing: false, progress: (done, total) => _vm.ReportImportProgress(done, total)));
            _vm.FinishImportProgress();
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("MTGS mapping dry-run:");
            sb.AppendLine($"Will update MtgsId on: {dry.UpdatedMtgsIds}");
            sb.AppendLine($"Already mapped (skipped): {dry.SkippedExisting}");
            sb.AppendLine($"Conflicts: {dry.Conflicts}");
            sb.AppendLine($"Unmatched: {(dry.UnmatchedLogPath != null ? "see log " + dry.UnmatchedLogPath : "0")} ");
            sb.AppendLine($"Errors: {dry.Errors}");
            sb.AppendLine();
            sb.AppendLine("Proceed to apply updates?\nClick Yes to apply only updates; No to also insert unmatched; Cancel to abort.");
            var choice = MessageBox.Show(this, sb.ToString(), "MTGS Mapping", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (choice == MessageBoxResult.Cancel) return;

            bool insertMissing = (choice == MessageBoxResult.No);
            _vm.StartImportProgress(insertMissing ? "MTGS apply + insert" : "MTGS apply");
            var apply = await Task.Run(() => CsvMainDbUpdater.ProcessMtgsMapping(dlg.FileName, dryRun: false, insertMissing: insertMissing, progress: (done, total) => _vm.ReportImportProgress(done, total)));
            _vm.FinishImportProgress();
            MessageBox.Show(this, $"MTGS mapping applied:\nUpdated MtgsId: {apply.UpdatedMtgsIds}\nInserted new: {apply.InsertedNew}\nSkipped existing: {apply.SkippedExisting}\nConflicts: {apply.Conflicts}\nErrors: {apply.Errors}\nUnmatched log: {(apply.UnmatchedLogPath ?? "(none)")}", "CSV Utility", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "CSV Utility Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ImportScryfallSet_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var input = Infrastructure.InputBoxDialogService.Instance.ShowInputDialog("Enter Scryfall set code (e.g., mom)", "Import Set", "");
            if (string.IsNullOrWhiteSpace(input)) return;
            if (_vm == null || string.IsNullOrEmpty(_vm.CurrentCollectionDir)) { MessageBox.Show(this, "Open a collection file first so the mainDb location is known."); return; }
            string dbPath = System.IO.Path.Combine(_vm.CurrentCollectionDir!, "mainDb.db");
            if (!File.Exists(dbPath)) { MessageBox.Show(this, "mainDb.db not found."); return; }
            bool forceReimport = (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift));
            var summary = await _vm.ImportService.ImportSetAsync(input.Trim(), forceReimport, dbPath, msg => _vm.SetStatus(msg));
            _vm.SetStatus($"Import {summary.SetCode}: inserted {summary.Inserted}, updated {summary.UpdatedExisting}, skipped {summary.Skipped}. Total fetched {summary.TotalFetched}{(summary.DeclaredCount.HasValue?"/"+summary.DeclaredCount.Value:"")}.");
        }
        catch (Exception ex)
        {
            _vm.SetStatus("Import error: " + ex.Message);
        }
    }

    private void ExportPlaysetNeedsGeneric_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string setCode = Infrastructure.InputBoxDialogService.Instance.ShowInputDialog("Enter source set code (e.g., DMR, MOM, BRO)", "Playset Needs Export", "DMR");
            if (string.IsNullOrWhiteSpace(setCode)) return;
            string path = PlaysetNeedsExporter.ExportPlaysetNeedsForSet(setCode.Trim());
            _vm.SetStatus($"{setCode.ToUpperInvariant()} playset needs exported: " + System.IO.Path.GetFileName(path));
            MessageBox.Show(this, $"Export complete for {setCode.ToUpperInvariant()}:\n{path}", "Playset Needs Export", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExportWantListMoxfield_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string path = Utilities.WantListExporter.ExportMoxfield(_vm.Cards);
            _vm.SetStatus($"Moxfield want list exported: {System.IO.Path.GetFileName(path)}");
            MessageBox.Show(this, $"Export complete:\n{path}", "Moxfield Want List Export", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExportWantListMoxfieldCsv_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string path = Utilities.WantListExporter.ExportMoxfieldCsv(_vm.Cards);
            _vm.SetStatus($"Moxfield collection exported: {System.IO.Path.GetFileName(path)}");
            MessageBox.Show(this, $"Export complete:\n{path}", "Moxfield Collection Export", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void MatchWantsCsv_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlgCollection = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select your Moxfield collection CSV",
                Filter = "CSV Files (*.csv)|*.csv|All Files (*.*)|*.*"
            };
            if (dlgCollection.ShowDialog(this) != true) return;

            var dlgWants = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select Moxfield wants CSV",
                Filter = "CSV Files (*.csv)|*.csv|All Files (*.*)|*.*"
            };
            if (dlgWants.ShowDialog(this) != true) return;

            var dlgSave = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save matches CSV as",
                Filter = "CSV Files (*.csv)|*.csv",
                FileName = "matches.csv",
                InitialDirectory = System.IO.Path.GetDirectoryName(dlgCollection.FileName)
            };
            if (dlgSave.ShowDialog(this) != true) return;

            _vm.StartImportProgress("Matching wants + fetching prices");
            var result = await Task.Run(() => Utilities.CsvWantsMatcher.MatchAsync(
                dlgCollection.FileName, dlgWants.FileName, dlgSave.FileName,
                progressCallback: (done, total) => _vm.ReportImportProgress(done, total)));
            _vm.FinishImportProgress();
            _vm?.SetStatus($"Wants match: {result.MatchCount} matches found.");
            MessageBox.Show(this,
                $"Collection entries: {result.CollectionCount}\nWanted cards: {result.WantsCount}\nMatches: {result.MatchCount}\n\nOutput: {result.OutputPath}",
                "Wants Match", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _vm?.FinishImportProgress();
            MessageBox.Show(this, ex.Message, "Wants Match Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string MakeSafeFileNamePart(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "binder";
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return cleaned.Trim();
    }

    private async void BatchExportBinderWantsMoxfield_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folderDlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select folder containing binder*.txt files"
            };
            if (folderDlg.ShowDialog(this) != true) return;

            string folderPath = folderDlg.FolderName;
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            {
                MessageBox.Show(this, "Selected folder is not valid.", "Batch Export", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var binderFiles = Directory.GetFiles(folderPath, "binder*.txt", SearchOption.TopDirectoryOnly)
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (binderFiles.Count == 0)
            {
                MessageBox.Show(this, "No binder*.txt files found in the selected folder.", "Batch Export", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int exported = 0;
            int failed = 0;
            var failures = new List<string>();
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

            foreach (var binderPath in binderFiles)
            {
                string binderName = Path.GetFileNameWithoutExtension(binderPath);
                try
                {
                    _vm.SetStatus($"Loading {binderName}...");
                    await _vm.LoadFromFileAsync(binderPath, awaitFullMetadata: true);

                    string safeName = MakeSafeFileNamePart(binderName);
                    string outputPath = Path.Combine(folderPath, $"want_list_moxfield_{safeName}_{timestamp}.csv");
                    Utilities.WantListExporter.ExportMoxfieldCsv(_vm.Cards, outputPath);
                    exported++;
                }
                catch (Exception ex)
                {
                    failed++;
                    failures.Add($"{binderName}: {ex.Message}");
                }
            }

            _vm.SetStatus($"Batch Moxfield wants export complete. Exported={exported}, Failed={failed}");

            var summary = new System.Text.StringBuilder();
            summary.AppendLine($"Processed: {binderFiles.Count}");
            summary.AppendLine($"Exported: {exported}");
            summary.AppendLine($"Failed: {failed}");
            summary.AppendLine();
            summary.AppendLine($"Output folder: {folderPath}");

            if (failures.Count > 0)
            {
                summary.AppendLine();
                summary.AppendLine("Failures:");
                foreach (var line in failures.Take(10)) summary.AppendLine(line);
                if (failures.Count > 10) summary.AppendLine($"... and {failures.Count - 10} more");
            }

            MessageBox.Show(this, summary.ToString(), "Batch Export Binder Wants (Moxfield)", MessageBoxButton.OK,
                failed == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Batch Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // Tools menu handlers (delegate to view model)
    private async void DeckPullReport_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlgDeck = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select Deck List (Goldfish export)",
                Filter = "Text Files (*.txt)|*.txt|All Files (*.*)|*.*"
            };
            if (dlgDeck.ShowDialog(this) != true) return;

            var dlgSave = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save pull report as",
                Filter = "Text Files (*.txt)|*.txt",
                FileName = "deck_pull_report.txt",
                InitialDirectory = System.IO.Path.GetDirectoryName(dlgDeck.FileName)
            };
            if (dlgSave.ShowDialog(this) != true) return;

            _vm?.SetStatus("Generating deck pull report...");
            _vm?.StartImportProgress("Deck pull report");
            var result = await Task.Run(async () =>
            {
                var deck = Utilities.DeckPullReportService.ParseDeckFile(dlgDeck.FileName);
                return await Utilities.DeckPullReportService.GenerateReportAsync(deck, dlgSave.FileName,
                    progressCallback: (done, total) => _vm.ReportImportProgress(done, total));
            });
            _vm?.FinishImportProgress();
            _vm?.SetStatus($"Deck pull: {result.Pulls.Count} pull lines, {result.Missing.Count} missing.");
            MessageBox.Show(this,
                $"Pull lines: {result.Pulls.Count}\nMissing cards: {result.Missing.Count}\n\nOutput: {result.OutputPath}",
                "Deck Pull Report", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Deck Pull Report Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Layout4x3_Click(object sender, RoutedEventArgs e) { if (_vm!=null) _vm.LayoutMode = "4x3"; }
    private void Layout3x3_Click(object sender, RoutedEventArgs e) { if (_vm!=null) _vm.LayoutMode = "3x3"; }
    private void Layout2x2_Click(object sender, RoutedEventArgs e) { if (_vm!=null) _vm.LayoutMode = "2x2"; }

    private void PriceModeNone_Click(object sender, RoutedEventArgs e) { if (_vm!=null) _vm.PriceDisplayMode = "None"; }
    private void PriceModeMissing_Click(object sender, RoutedEventArgs e) { if (_vm!=null) _vm.PriceDisplayMode = "Missing"; }
    private void PriceModeAll_Click(object sender, RoutedEventArgs e) { if (_vm!=null) _vm.PriceDisplayMode = "All"; }

    private void SetPagesPerBinder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string input = Infrastructure.InputBoxDialogService.Instance.ShowInputDialog("Pages per binder?", "Set Pages/Binder", _vm.PagesPerBinder.ToString());
            if (string.IsNullOrWhiteSpace(input)) return;
            if (int.TryParse(input.Trim(), out int pages) && pages > 0 && pages <= 1000)
            {
                _vm.PagesPerBinder = pages;
                _vm.SetStatus($"Pages/Binder set to {pages}");
            }
            else MessageBox.Show(this, "Invalid number.", "Pages/Binder", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) { _vm.SetStatus("Pages/Binder error: " + ex.Message); }
    }

    private void JumpDialog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string binder = Infrastructure.InputBoxDialogService.Instance.ShowInputDialog("Binder number? (blank to keep current)", "Jump", "");
            string page = Infrastructure.InputBoxDialogService.Instance.ShowInputDialog("Page number? (blank to keep current)", "Jump", "");
            bool binderChanged = false; bool pageChanged = false;
            if (!string.IsNullOrWhiteSpace(binder) && int.TryParse(binder.Trim(), out int b) && b > 0)
            {
                _vm.JumpBinderInput = b.ToString(); binderChanged = true;
            }
            if (!string.IsNullOrWhiteSpace(page) && int.TryParse(page.Trim(), out int p) && p > 0)
            {
                _vm.JumpPageInput = p.ToString(); pageChanged = true;
            }
            if (binderChanged || pageChanged) _vm.JumpToPageCommand.Execute(null);
        }
        catch (Exception ex) { _vm.SetStatus("Jump error: " + ex.Message); }
    }

    // Auto-import all binder set codes not present in mainDb
    private async void AutoImportMissingSets_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_vm == null || string.IsNullOrEmpty(_vm.CurrentCollectionDir)) { _vm?.SetStatus("Open a collection first."); return; }
            string dbPath = System.IO.Path.Combine(_vm.CurrentCollectionDir!, "mainDb.db");
            HashSet<string> binderSets = _vm.GetCurrentSetCodes();
            bool confirm = true;
            bool ConfirmPrompt(string list) => MessageBox.Show(this, $"Import missing sets into mainDb?\n\n{list}", "Auto Import", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
            await _vm.ImportService.AutoImportMissingAsync(binderSets, dbPath, confirm, list => ConfirmPrompt(list), _vm);
        }
        catch (Exception ex)
        {
            _vm?.SetStatus("Auto import error: " + ex.Message);
        }
    }

    public MainWindow()
    {
        // Invoke generated InitializeComponent if present; otherwise manual load (design-time / analysis env may lack XAML compile)
        var init = GetType().GetMethod("InitializeComponent", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        if (init != null)
        {
            init.Invoke(this, null);
        }
        else
        {
            try
            {
                var resourceLocater = new Uri("/Enfolderer.App;component/MainWindow.xaml", UriKind.Relative);
                Application.LoadComponent(this, resourceLocater);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WPF] Manual XAML load failed: {ex.Message}");
                throw;
            }
        }
        _vm = new BinderViewModel();
        DataContext = _vm;
#if SELF_TESTS
        try
        {
            AppRuntimeFlags.DisableImageFetching = true; // suppress external HTTP during self tests
            int fail = Enfolderer.App.Tests.SelfTests.RunAll();
            if (fail==0) Debug.WriteLine("[SELF-TEST] All self tests passed.");
            else Debug.WriteLine($"[SELF-TEST] FAILURES: {fail}");
        }
        catch (Exception stex)
        {
            Debug.WriteLine($"[SELF-TEST] Exception: {stex.Message}");
        }
        finally
        {
            AppRuntimeFlags.DisableImageFetching = false; // re-enable for normal runtime use
        }
#endif
    }

    private async void OpenCollection_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open Binder Text File (ie binder_alt_arts.txt)",
            Filter = "TXT Files (*.txt)|*.txt"
        };
        if (dlg.ShowDialog(this) == true)
        {
            try
            {
                if (_vm != null)
                {
                    // Force reload logic now handled internally by LoadFromFileAsync (it calls Reload)
                    await _vm.LoadFromFileAsync(dlg.FileName);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void OpenLandsViewer_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Open Lands CSV File",
                Filter = "CSV Files (*.csv)|*.csv|All Files (*.*)|*.*"
            };
            if (dlg.ShowDialog(this) == true)
            {
                var win = new LandsWindow(dlg.FileName);
                win.Owner = this;
                win.Show();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Lands Viewer Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenTokensViewer_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Open Tokens CSV File",
                Filter = "CSV Files (*.csv)|*.csv|All Files (*.*)|*.*"
            };
            if (dlg.ShowDialog(this) == true)
            {
                var win = new Tokens.TokensWindow(dlg.FileName);
                win.Owner = this;
                win.Show();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Tokens Viewer Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BackfillZeroQty_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_vm == null) return;
            int inserted = _vm.RunBackfillZeroQty(threshold: 150000);
            _vm.SetStatus($"Backfill inserted {inserted} rows (id < 150000).");
        }
        catch (Exception ex)
        {
            _vm?.SetStatus("Backfill failed: " + ex.Message);
        }
    }

    private void SetQtyEqualsCardId_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_vm == null) return;
            var confirm = MessageBox.Show(this, "This will set Qty = CardId for ALL rows in mtgstudio.collection beside the EXE. Continue?", "Confirm Update", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.OK) return;
            int updated = _vm.RunSetQtyEqualsCardId();
            _vm.SetStatus($"Set Qty=CardId updated {updated} rows.");
        }
        catch (Exception ex)
        {
            _vm?.SetStatus("Set Qty=CardId failed: " + ex.Message);
        }
    }

    private void RestoreCollectionBackup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_vm == null) return;
            var confirm = MessageBox.Show(this, "Restore mtgstudio.collection from .bak beside the EXE?", "Confirm Restore", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.OK) return;
            bool ok = _vm.RunRestoreCollectionBackup();
            _vm.SetStatus(ok ? "Restore completed from .bak." : "Restore failed or backup not found.");
        }
        catch (Exception ex)
        {
            _vm?.SetStatus("Restore failed: " + ex.Message);
        }
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        if (_vm == null) { base.OnPreviewMouseWheel(e); return; }
        try
        {
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            int delta = e.Delta; // >0 wheel up, <0 wheel down
            if (ctrl)
            {
                // Ctrl + wheel = binder jump
                if (delta > 0)
                {
                    if (_vm.PrevBinderCommand.CanExecute(null)) _vm.PrevBinderCommand.Execute(null);
                }
                else if (delta < 0)
                {
                    if (_vm.NextBinderCommand.CanExecute(null)) _vm.NextBinderCommand.Execute(null);
                }
            }
            else if (shift)
            {
                // Shift + wheel = set boundary jump
                if (delta > 0)
                {
                    if (_vm.PrevSetCommand.CanExecute(null)) _vm.PrevSetCommand.Execute(null);
                }
                else if (delta < 0)
                {
                    if (_vm.NextSetCommand.CanExecute(null)) _vm.NextSetCommand.Execute(null);
                }
            }
            else
            {
    
    
                // No modifier = normal page navigation
                if (delta > 0)
                {
                    if (_vm.PrevCommand.CanExecute(null)) _vm.PrevCommand.Execute(null);
                }
                else if (delta < 0)
                {
                    if (_vm.NextCommand.CanExecute(null)) _vm.NextCommand.Execute(null);
                }
            }
            e.Handled = true; // prevent default scroll (there's no scroll viewer anyway)
        }
        finally
        {
            base.OnPreviewMouseWheel(e);
        }
    }

    private void RefreshQuantities_Click(object sender, RoutedEventArgs e)
    {
    _vm?.RefreshQuantities();
    }

    private void CardSlot_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (_vm == null) return;
            if (sender is not Border b) return;
            if (b.DataContext is not CardSlot slot) return;
            _vm.ToggleCardQuantity(slot);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UI] Click handler error: {ex.Message}");
        }
    }

    private void SearchNameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _vm?.PerformSearchNext();
            e.Handled = true;
        }
    }

    private void SearchNameBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox tb)
            tb.SelectAll();
    }

    private void SearchNameBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox tb && !tb.IsKeyboardFocusWithin)
        {
            tb.Focus();
            e.Handled = true;
        }
    }

    private void SearchButton_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        _vm?.PerformSearchNextSet();
        e.Handled = true;
    }
}

