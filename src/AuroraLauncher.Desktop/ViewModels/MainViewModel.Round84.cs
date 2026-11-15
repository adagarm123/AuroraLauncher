using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using AuroraCore;
using AuroraCore.Models;
using AuroraCore.Services;
using AuroraCore.Utils;

namespace AuroraLauncher.Desktop.ViewModels;

/// <summary>
/// Round-84: keeping the instance list and the folders on disk in agreement.
///
/// One rule decides everything here: <c>instances.json</c> is the source of
/// truth, but a FOLDER is the instance's real body — so a delete may only drop
/// the record after the folder is gone, and a scan may only ADD records (never
/// delete folders it does not understand).
/// </summary>
public partial class MainViewModel
{
    /// <summary>Recursive folder size in MB for the delete confirmation. Runs on
    /// a worker thread: a 20 GB instance is ~50k files and would stall the UI.</summary>
    private static long FolderSizeMb(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return 0;
            long total = 0;
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; } catch { /* vanished */ }
            }
            return total / 1024 / 1024;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Round-84: "扫描未注册实例". Compares instances.json with the
    /// folders under instances/ and offers to fix both directions:
    /// • folder without a record → re-register it (keeping its original Id);
    /// • record without a folder → offer to drop the dead link;
    /// • unknown folders (no marker) are REPORTED, never touched.
    /// Nothing is added without confirmation, because a scan that silently
    /// imports half-deleted folders is its own kind of data loss.</summary>
    public async Task ScanUnregisteredInstancesAsync()
    {
        if (IsBusy) { Status = "有任务正在进行中，请稍后再试"; return; }
        IsBusy = true;
        Status = "正在扫描实例文件夹…";
        try
        {
            var scan = await Task.Run(() => _svc.Instances.Scan()).ConfigureAwait(true);
            var report = new StringBuilder();
            if (scan.Recovered.Count > 0) report.AppendLine($"· 未注册的实例文件夹：{scan.Recovered.Count} 个");
            if (scan.MissingIds.Count > 0) report.AppendLine($"· 列表里有、文件夹已丢失：{scan.MissingIds.Count} 个");
            if (scan.CorruptedFolders.Count > 0) report.AppendLine($"· 元数据损坏、无法读取：{scan.CorruptedFolders.Count} 个");
            if (scan.UnrecognizedFolders.Count > 0) report.AppendLine($"· 陌生文件夹（未做任何改动）：{scan.UnrecognizedFolders.Count} 个");

            if (report.Length == 0)
            {
                Status = "扫描完成：列表与磁盘完全一致，没有幽灵实例或死链";
                AddLog("[Instance] 扫描：一致");
                MessageBox.Show(Application.Current.MainWindow,
                    $"实例列表与 {LauncherConfig.Instance.InstancesDir} 里的文件夹完全一致。\n\n" +
                    $"共 {Instances.Count} 个实例，没有发现未注册的文件夹或丢失的目录。",
                    "扫描实例", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            AddLog("[Instance] 扫描结果：" + report.ToString().Replace("\r", "").Replace("\n", " "));

            // ---- 1. 恢复未注册的实例（保留原 Id） ----
            if (scan.Recovered.Count > 0)
            {
                var names = string.Join("、", scan.Recovered.Take(6).Select(i => i.Name)) +
                            (scan.Recovered.Count > 6 ? " …" : "");
                var answer = MessageBox.Show(Application.Current.MainWindow,
                    $"发现 {scan.Recovered.Count} 个文件夹里有实例元数据，但列表中没有它们：\n\n{names}\n\n" +
                    "要把它们重新加入列表吗？（只读这些文件夹里的 .aurora-instance.json，不会移动或删除任何文件）",
                    "发现未注册的实例", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
                if (answer == MessageBoxResult.Yes)
                {
                    var added = 0;
                    foreach (var inst in scan.Recovered)
                    {
                        if (Instances.Any(i => string.Equals(i.Id, inst.Id, StringComparison.OrdinalIgnoreCase)))
                            continue;
                        Instances.Add(inst);
                        added++;
                    }
                    // Persist immediately: the whole point is to make the JSON agree
                    // with the disk, not to keep it in memory only.
                    _svc.Instances.SaveAll(Instances.ToList());
                    if (added > 0) SelectedInstance = scan.Recovered[0];
                    Status = $"已恢复 {added} 个未注册实例（保留原 Id，存档与模组关联不变）";
                    ShowToast(Status);
                }
            }

            // ---- 2. 死链：列表里有、文件夹没了 ----
            if (scan.MissingIds.Count > 0)
            {
                var dead = Instances.Where(i => scan.MissingIds.Contains(i.Id, StringComparer.OrdinalIgnoreCase)).ToList();
                var names = string.Join("、", dead.Take(6).Select(i => i.Name)) + (dead.Count > 6 ? " …" : "");
                var answer = MessageBox.Show(Application.Current.MainWindow,
                    $"有 {dead.Count} 个实例的文件夹已经不存在了（可能被手动删除）：\n\n{names}\n\n" +
                    "要把这些死链从列表里移除吗？（只删列表记录，不删任何文件）",
                    "清理死链", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.Yes);
                if (answer == MessageBoxResult.Yes)
                {
                    foreach (var inst in dead)
                    {
                        // DeleteChecked: folder is already gone → it only drops metadata.
                        _svc.Instances.DeleteChecked(inst.Id);
                        Instances.Remove(inst);
                    }
                    if (SelectedInstance == null || !Instances.Contains(SelectedInstance))
                        SelectedInstance = Instances.FirstOrDefault();
                    Status = $"已从列表移除 {dead.Count} 条死链";
                }
            }

            // ---- 3. 陌生/损坏：只报告，不动 ----
            if (scan.UnrecognizedFolders.Count > 0 || scan.CorruptedFolders.Count > 0)
            {
                var msg = new StringBuilder();
                if (scan.UnrecognizedFolders.Count > 0)
                    msg.AppendLine($"未识别（没有 {InstanceManager.MetaFileName}，可能是你手动复制进来的文件夹，也可能不是实例）：\n  " +
                                   string.Join("\n  ", scan.UnrecognizedFolders.Take(8)) +
                                   (scan.UnrecognizedFolders.Count > 8 ? "\n  …" : "") + "\n");
                if (scan.CorruptedFolders.Count > 0)
                    msg.AppendLine($"元数据损坏（无法解析，已跳过）：\n  " +
                                   string.Join("\n  ", scan.CorruptedFolders.Take(8)) + "\n");
                msg.AppendLine("启动器没有改动这些文件夹。如果其中确实是你想要的实例，请把它的完整路径发给我，我按名字手工加回列表。");
                MessageBox.Show(Application.Current.MainWindow, msg.ToString(),
                    "未处理的文件夹", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            Status = "扫描失败: " + ex.Message;
            AddLog("[Instance] 扫描失败: " + ex);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
