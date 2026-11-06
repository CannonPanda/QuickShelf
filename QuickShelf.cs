using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using System.Text;

[Serializable]
public class ShortcutEntry {
    public string Name { get; set; }
    public string Path { get; set; }
    public string Arguments { get; set; }
    public string WorkingDirectory { get; set; }
    public bool IsGroup { get; set; }
    public List<ShortcutEntry> Children = new List<ShortcutEntry>();
    public ShortcutEntry Clone() {
        var copy = (ShortcutEntry)MemberwiseClone();
        copy.Children = new List<ShortcutEntry>();
        foreach (var child in Children) copy.Children.Add(child.Clone());
        return copy;
    }
    public override string ToString() { return IsGroup ? "▸ " + Name : Name; }
}
static class ShortcutResolver {
    public static ShortcutEntry Resolve(string path) {
        var entry = new ShortcutEntry { Path = path };
        if (!String.Equals(System.IO.Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase)) return entry;
        object shell = null, link = null;
        try {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true));
            link = shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
            var type = link.GetType();
            string target = type.InvokeMember("TargetPath", BindingFlags.GetProperty, null, link, null) as string;
            if (String.IsNullOrWhiteSpace(target)) throw new IOException("该快捷方式没有可解析的文件目标。");
            target = Environment.ExpandEnvironmentVariables(target);
            if (!System.IO.Path.IsPathRooted(target)) target = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path), target);
            entry.Path = System.IO.Path.GetFullPath(target);
            if (!File.Exists(entry.Path) && !Directory.Exists(entry.Path)) throw new IOException("快捷方式指向的目标不存在：" + entry.Path);
            entry.Arguments = type.InvokeMember("Arguments", BindingFlags.GetProperty, null, link, null) as string;
            entry.WorkingDirectory = type.InvokeMember("WorkingDirectory", BindingFlags.GetProperty, null, link, null) as string;
            return entry;
        } finally {
            if (link != null && Marshal.IsComObject(link)) Marshal.FinalReleaseComObject(link);
            if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }
}
[Serializable]
public class Settings {
    public List<ShortcutEntry> Entries = new List<ShortcutEntry>();
}
static class Storage {
    public static string Fingerprint(string path) {
        if (!File.Exists(path)) return "missing";
        using (var stream = File.OpenRead(path)) using (var hash = SHA256.Create()) return Convert.ToBase64String(hash.ComputeHash(stream));
    }
    public static string DraftFile { get { return System.IO.Path.Combine(Folder, "unsaved.json"); } }
    public static string WriteChecked(Settings settings, string path, string expected) {
        using (var gate = new Mutex(false, "Local\\QuickShelf-Settings")) {
            bool acquired = false;
            try {
                try { acquired = gate.WaitOne(5000); } catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new IOException("配置正被其他程序写入，请稍后重试。");
                if (Fingerprint(path) != expected) throw new IOException("配置在编辑期间被其他程序修改，已阻止覆盖。请先备份 unsaved.json 中的当前修改后再重新打开管理界面。");
                Write(settings, path); return Fingerprint(path);
            } finally { if (acquired) gate.ReleaseMutex(); }
        }
    }
    static void Clean(List<ShortcutEntry> entries) {
        if (entries.Exists(e => e == null || (!e.IsGroup && String.IsNullOrWhiteSpace(e.Path)))) throw new InvalidDataException("配置中存在无效条目，已停止加载以避免丢失数据。请检查备份。");
        foreach (var entry in entries) {
            if (entry.Children == null) entry.Children = new List<ShortcutEntry>();
            if (String.IsNullOrWhiteSpace(entry.Name)) entry.Name = entry.IsGroup ? "未命名分组" : entry.Path;
            if (entry.IsGroup) Clean(entry.Children);
        }
    }
    static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = 16777216, RecursionLimit = 256 };
    public static readonly string Folder = AppDomain.CurrentDomain.BaseDirectory;
    public static readonly string FileName = System.IO.Path.Combine(Folder, "shortcuts.json");
    public static string Serialize(Settings settings) { return Serializer.Serialize(settings); }
    public static Settings Read(string path) {
        if (!File.Exists(path)) return new Settings();
        {
            string json = File.ReadAllText(path, Encoding.UTF8);
            var structure = Serializer.DeserializeObject(json) as Dictionary<string, object>;
            if (structure == null || !structure.ContainsKey("Entries") || structure["Entries"] == null) throw new InvalidDataException("JSON 配置缺少 Entries 列表，已停止加载以保护数据。");
            var settings = Serializer.Deserialize<Settings>(json);
            if (settings == null || settings.Entries == null) throw new InvalidDataException("JSON 配置无效。");
            Clean(settings.Entries);
            return settings;
        }
    }
    public static void Write(Settings settings, string path) {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            byte[] data = new UTF8Encoding(false).GetBytes(Serialize(settings));
            using (var stream = File.Create(temp)) { stream.Write(data, 0, data.Length); stream.Flush(true); }
            if (File.Exists(path)) {
                if (path == FileName) {
                    string history = System.IO.Path.Combine(Folder, "history"); Directory.CreateDirectory(history);
                    File.Copy(path, System.IO.Path.Combine(history, "shortcuts-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fffffff") + ".json"));
                    var versions = new DirectoryInfo(history).GetFiles("shortcuts-*.json");
                    Array.Sort(versions, (a,b) => b.Name.CompareTo(a.Name));
                    for (int i = 50; i < versions.Length; i++) versions[i].Delete();
                }
                File.Replace(temp, path, path + ".bak");
            }
            else File.Move(temp, path);
        } finally {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
static class Favorites {
    public static bool IsSaved(Settings settings, ShortcutEntry entry) {
        if (entry.IsGroup) return false;
        try {
            string path = entry.Path;
            if (String.Equals(System.IO.Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase)) path = ShortcutResolver.Resolve(path).Path;
            return Contains(settings.Entries, Canonical(path));
        } catch { return false; }
    }
    static string Canonical(string path) {
        string full = System.IO.Path.GetFullPath(path);
        return full.Length > System.IO.Path.GetPathRoot(full).Length ? full.TrimEnd('\\','/') : full;
    }
    static bool Contains(List<ShortcutEntry> entries, string path) {
        foreach (var item in entries) {
            if (item.IsGroup) { if (Contains(item.Children, path)) return true; }
            else if (String.Equals(Canonical(item.Path), path, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
    public static Settings Add(ShortcutEntry source) {
        if (source.IsGroup) throw new IOException("分组没有可收藏的实际路径。");
        string revision = Storage.Fingerprint(Storage.FileName);
        var settings = Storage.Read(Storage.FileName);
        var entry = String.Equals(System.IO.Path.GetExtension(source.Path), ".lnk", StringComparison.OrdinalIgnoreCase) ? ShortcutResolver.Resolve(source.Path) : source.Clone();
        entry.Path = Canonical(entry.Path);
        if (!File.Exists(entry.Path) && !Directory.Exists(entry.Path)) throw new IOException("要收藏的文件或文件夹不存在。");
        if (Contains(settings.Entries, entry.Path)) return settings;
        entry.Name = String.IsNullOrWhiteSpace(source.Name) ? System.IO.Path.GetFileName(entry.Path) : source.Name;
        settings.Entries.Add(entry);
        Storage.WriteChecked(settings, Storage.FileName, revision);
        return settings;
    }
    static void Remove(List<ShortcutEntry> entries, string path) {
        for (int i = entries.Count - 1; i >= 0; i--) {
            if (entries[i].IsGroup) Remove(entries[i].Children, path);
            else if (String.Equals(Canonical(entries[i].Path), path, StringComparison.OrdinalIgnoreCase)) entries.RemoveAt(i);
        }
    }
    public static Settings Toggle(ShortcutEntry source) {
        if (source.IsGroup) throw new IOException("分组没有实际路径。");
        string revision = Storage.Fingerprint(Storage.FileName);
        var settings = Storage.Read(Storage.FileName);
        var entry = String.Equals(System.IO.Path.GetExtension(source.Path), ".lnk", StringComparison.OrdinalIgnoreCase) ? ShortcutResolver.Resolve(source.Path) : source.Clone();
        entry.Path = Canonical(entry.Path);
        if (Contains(settings.Entries, entry.Path)) Remove(settings.Entries, entry.Path);
        else {
            if (!File.Exists(entry.Path) && !Directory.Exists(entry.Path)) throw new IOException("要收藏的文件或文件夹不存在。");
            entry.Name = String.IsNullOrWhiteSpace(source.Name) ? System.IO.Path.GetFileName(entry.Path) : source.Name;
            settings.Entries.Add(entry);
        }
        Storage.WriteChecked(settings, Storage.FileName, revision); return settings;
    }
}
static class Diagnostics {
    public static void Log(Exception ex) {
        try { Directory.CreateDirectory(Storage.Folder); File.AppendAllText(System.IO.Path.Combine(Storage.Folder,"errors.log"), DateTime.Now.ToString("o") + "\r\n" + ex + "\r\n\r\n"); } catch { }
    }
}
static class Launcher {
    static void Start(ProcessStartInfo info) {
        var process = Process.Start(info);
        if (process != null) process.Dispose();
    }
    public static string LocationArguments(string path) { return "/select,\"" + path + "\""; }
    public static void Open(ShortcutEntry entry, bool locate) {
        try {
            string path = entry.Path;
            if (!File.Exists(path) && !Directory.Exists(path)) throw new IOException("文件或文件夹不存在，或者所在磁盘尚未连接。\n\n" + path);
            if (locate) {
                // A drive root has no parent to select it in.
                if (String.Equals(System.IO.Path.GetPathRoot(path), path, StringComparison.OrdinalIgnoreCase))
                    Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
                else Start(new ProcessStartInfo("explorer.exe", LocationArguments(path)) { UseShellExecute = true });
            } else Start(new ProcessStartInfo(path) { UseShellExecute = true, Arguments = entry.Arguments ?? "", WorkingDirectory = entry.WorkingDirectory ?? "" });
        } catch (Exception ex) { MessageBox.Show(ex.Message, "快捷托盘", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
}
sealed class FolderChild {
    public ShortcutEntry Entry;
    public bool IsDirectory;
}
static class FolderBrowser {
    public static List<FolderChild> Read(string path) {
        var result = new List<FolderChild>();
        foreach (var child in new DirectoryInfo(path).EnumerateFileSystemInfos()) {
            // Match Explorer's usual view; hidden and system files stay out of the quick menu.
            var attributes = child.Attributes;
            if ((attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
            result.Add(new FolderChild { Entry = new ShortcutEntry { Name = child.Name, Path = child.FullName }, IsDirectory = (attributes & FileAttributes.Directory) != 0 });
        }
        result.Sort(delegate(FolderChild a, FolderChild b) {
            if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;
            return StringComparer.CurrentCultureIgnoreCase.Compare(a.Entry.Name, b.Entry.Name);
        });
        return result;
    }
}
sealed class ShortcutList : ListBox {
    Point down;
    bool pressed, deferredSelection, dragged;
    public event EventHandler DragSelection;
    protected override void WndProc(ref Message m) {
        if (m.Msg == 0x200 && pressed && (m.WParam.ToInt64() & 1) != 0) {
            int raw = m.LParam.ToInt32();
            OnMouseMove(new MouseEventArgs(MouseButtons.Left,0,(short)(raw & 65535),(short)(raw >> 16),0)); return;
        }
        if (m.Msg == 0x201) {
            int raw = m.LParam.ToInt32(); var point = new Point((short)(raw & 65535), (short)(raw >> 16));
            int index = IndexFromPoint(point);
            down = point; pressed = index >= 0; dragged = false;
            if (ModifierKeys == Keys.None && index >= 0 && GetSelected(index) && SelectedIndices.Count > 1) {
                deferredSelection = true; Capture = true;
                OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0)); return;
            }
        }
        base.WndProc(ref m);
    }
    protected override void OnMouseDown(MouseEventArgs e) {
        down = e.Location; pressed = e.Button == MouseButtons.Left && IndexFromPoint(down) >= 0; dragged = false;
        base.OnMouseDown(e);
    }
    protected override void OnMouseMove(MouseEventArgs e) {
        base.OnMouseMove(e);
        var size = SystemInformation.DragSize;
        if (pressed && e.Button == MouseButtons.Left && !new Rectangle(down.X-size.Width/2,down.Y-size.Height/2,size.Width,size.Height).Contains(e.Location)) {
            pressed = false; dragged = true; Capture = false;
            if (DragSelection != null) DragSelection(this,EventArgs.Empty);
            deferredSelection = false;
        }
    }
    protected override void OnMouseUp(MouseEventArgs e) {
        if (deferredSelection && !dragged && ModifierKeys == Keys.None) {
            int index = IndexFromPoint(e.Location); ClearSelected(); if (index >= 0) SetSelected(index,true);
        }
        pressed = false; deferredSelection = false; Capture = false; base.OnMouseUp(e);
    }
}
sealed class Manager : Form {
    sealed class SelectionDrag { public Manager Owner; public List<ShortcutEntry> Items; }
    const string DragFormat = "QuickShelf.Selection";
    int dropIndex = -1, lastScroll;
    ShortcutEntry dropGroup;
    readonly Font uiFont = new Font("Microsoft YaHei UI", 9F);
    bool batchAdding;
    int batchSelection;
    int editingIndex = -1;
    bool refreshing, discardChanges, saved;
    bool ready;
    string lastDraft;
    readonly List<ShortcutEntry> original = new List<ShortcutEntry>();
    readonly List<ShortcutEntry> rootEntries = new List<ShortcutEntry>();
    readonly Label location = new Label { Dock = DockStyle.Fill };
    ShortcutEntry currentGroup;
    readonly ShortcutList list = new ShortcutList { Dock = DockStyle.Fill, IntegralHeight = false, AllowDrop = true, SelectionMode = SelectionMode.MultiExtended, DrawMode = DrawMode.OwnerDrawFixed };
    readonly TextBox name = new TextBox { Dock = DockStyle.Fill };
    readonly TextBox path = new TextBox { Dock = DockStyle.Fill };
    readonly Action<Settings> commit;
    List<ShortcutEntry> entries;
    public Manager(Settings settings, Action<Settings> save) : this(settings, save, settings) { }
    public Manager(Settings settings, Action<Settings> save, Settings baseline) {
        commit = save;
        entries = rootEntries;
        foreach (var e in settings.Entries) entries.Add(e.Clone());
        foreach (var e in baseline.Entries) original.Add(e.Clone());
        Text = "快捷托盘 · 管理快捷项"; ClientSize = new Size(980, 510); MinimumSize = new Size(950, 440);
        StartPosition = FormStartPosition.CenterScreen; Font = uiFont; AutoScaleMode = AutoScaleMode.Dpi;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 6 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.Controls.Add(location, 0, 0);
        layout.Controls.Add(list, 0, 1);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 6, 0, 0), WrapContents = false };
        AddButton(buttons, "添加文件", delegate {
            using (var dialog = new OpenFileDialog { Multiselect = true, Title = "选择常用文件" })
                if (dialog.ShowDialog(this) == DialogResult.OK) AddMany(dialog.FileNames);
        });
        AddButton(buttons, "添加文件夹", delegate {
            using (var dialog = new FolderBrowserDialog { Description = "选择常用文件夹", ShowNewFolderButton = false })
                if (dialog.ShowDialog(this) == DialogResult.OK) Add(dialog.SelectedPath);
        });
        AddButton(buttons, "删除", delegate {
            var selected = SelectedEntries(); int i = list.SelectedIndex;
            if (selected.Exists(item => item.IsGroup && item.Children.Count > 0) && MessageBox.Show(this, "删除选中的快捷项及分组内的全部快捷项？实际文件不会删除。", "删除分组", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            foreach (var item in selected) entries.Remove(item);
            RefreshList(Math.Min(i, entries.Count - 1));
        });
        AddButton(buttons, "新建分组", delegate { NewGroup(); });
        AddButton(buttons, "返回上级", delegate { Navigate(FindParent(rootEntries, currentGroup, null)); });
        AddButton(buttons, "移动到…", delegate { MoveToGroup(); });
        layout.Controls.Add(buttons, 0, 2);
        var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52)); fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fields.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); fields.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        fields.Controls.Add(new Label { Text = "名称", AutoSize = true }, 0, 0); fields.Controls.Add(name, 1, 0);
        fields.Controls.Add(new Label { Text = "路径", AutoSize = true }, 0, 1); fields.Controls.Add(path, 1, 1);
        layout.Controls.Add(fields, 0, 3);
        var edits = new FlowLayoutPanel { Dock = DockStyle.Fill };
        AddButton(edits, "更新所选项", delegate { UpdateSelected(); });
        AddButton(edits, "按路径添加", delegate { Add(path.Text); });
        AddButton(edits, "定位所选项", delegate { if (list.SelectedIndex >= 0 && !entries[list.SelectedIndex].IsGroup) Launcher.Open(entries[list.SelectedIndex], true); });
        layout.Controls.Add(edits, 0, 4);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        AddButton(footer, "保存并关闭", delegate {
            try { commit(Snapshot()); saved = true; DeleteDraft(); Close(); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        });
        AddButton(footer, "取消", delegate { discardChanges = true; DeleteDraft(); Close(); });
        layout.Controls.Add(footer, 0, 5); Controls.Add(layout);
        list.SelectedIndexChanged += delegate {
            if (refreshing) return;
            int next = list.SelectedIndex;
            if (!CommitPending()) { refreshing = true; try { list.SelectedIndex = editingIndex; } finally { refreshing = false; } return; }
            LoadFields(next);
        };
        list.DragSelection += delegate {
            if (!CommitPending()) return;
            var selected = SelectedEntries(); if (selected.Count == 0) return;
            var data = new DataObject(); data.SetData(DragFormat, false, new SelectionDrag { Owner = this, Items = selected });
            try { list.DoDragDrop(data, DragDropEffects.Move); } finally { ClearDropMarker(); }
        };
        list.DragEnter += OnListDragOver;
        list.DragOver += OnListDragOver;
        list.DragLeave += delegate { ClearDropMarker(); };
        list.DragDrop += DropSelection;
        list.ItemHeight = Math.Max(24, Font.Height + 8);
        list.DrawItem += DrawRow;
        list.DoubleClick += delegate {
            if (!CommitPending() || list.SelectedIndices.Count != 1) return;
            var entry = entries[list.SelectedIndex];
            if (entry.IsGroup) Navigate(entry); else Launcher.Open(entry, false);
        };
        RefreshList(entries.Count > 0 ? 0 : -1); ready = true;
    }
    void AddButton(Control parent, string text, EventHandler action) {
        var b = new Button { Text = text, AutoSize = true, Height = 29, Margin = new Padding(0, 0, 8, 0) };
        b.Click += delegate(object sender, EventArgs e) { if (text == "取消" || text == "按路径添加" || CommitPending()) action(sender, e); }; parent.Controls.Add(b);
    }
    string Normalize(string value) {
        string p = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
        if (!System.IO.Path.IsPathRooted(p) || (!File.Exists(p) && !Directory.Exists(p))) throw new IOException("请选择存在的文件或文件夹。");
        p = System.IO.Path.GetFullPath(p);
        return p.Length > System.IO.Path.GetPathRoot(p).Length ? p.TrimEnd('\\') : p;
    }
    void Add(string value) {
        try {
            var resolved = ShortcutResolver.Resolve(Normalize(value));
            string p = resolved.Path;
            int existing = entries.FindIndex(e => !e.IsGroup && String.Equals(e.Path, p, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0) { if (batchAdding) batchSelection = existing; else list.SelectedIndex = existing; return; }
            string label = System.IO.Path.GetFileName(p); if (String.IsNullOrEmpty(label)) label = p;
            resolved.Name = label; entries.Add(resolved);
            if (batchAdding) batchSelection = entries.Count - 1; else RefreshList(entries.Count - 1);
        } catch (Exception ex) { MessageBox.Show(this, ex.Message, "无法添加"); }
    }
    void AddMany(string[] files) {
        batchAdding = true; batchSelection = list.SelectedIndex;
        try { foreach (string file in files) Add(file); }
        finally { batchAdding = false; RefreshList(batchSelection); }
    }
    bool CommitPending() {
        int i = editingIndex; if (i < 0 || i >= entries.Count) return true;
        if (name.Text == entries[i].Name && path.Text == entries[i].Path) return true;
        try {
            if (String.IsNullOrWhiteSpace(name.Text)) throw new IOException("名称不能为空。");
            if (entries[i].IsGroup) {
                entries[i].Name = name.Text.Trim();
                refreshing = true;
                var selection = SelectedEntries();
                try { list.Items[i] = entries[i]; RestoreSelection(selection); } finally { refreshing = false; }
                SaveDraft();
                return true;
            }
            var resolved = path.Text == entries[i].Path ? entries[i].Clone() : ShortcutResolver.Resolve(Normalize(path.Text));
            string p = resolved.Path;
            int duplicate = entries.FindIndex(e => !e.IsGroup && String.Equals(e.Path, p, StringComparison.OrdinalIgnoreCase));
            if (duplicate >= 0 && duplicate != i) throw new IOException("列表中已有这个路径。");
            var selectionBefore = SelectedEntries();
            resolved.Name = name.Text.Trim();
            entries[i].Name = resolved.Name; entries[i].Path = resolved.Path;
            entries[i].Arguments = resolved.Arguments; entries[i].WorkingDirectory = resolved.WorkingDirectory;
            refreshing = true;
            try { list.Items[i] = entries[i]; RestoreSelection(selectionBefore); } finally { refreshing = false; }
            SaveDraft();
            return true;
        } catch (Exception ex) { MessageBox.Show(this, ex.Message, "无法更新"); return false; }
    }
    void UpdateSelected() { if (CommitPending()) RefreshList(list.SelectedIndex); }
    void LoadFields(int i) {
        if (list.SelectedIndices.Count > 1) i = -1;
        editingIndex = i; name.Enabled = list.SelectedIndices.Count <= 1;
        name.Text = i < 0 ? "" : entries[i].Name;
        path.Enabled = list.SelectedIndices.Count <= 1 && (i < 0 || !entries[i].IsGroup);
        path.Text = i < 0 || entries[i].IsGroup ? "" : entries[i].Path;
    }
    protected override void OnFormClosing(FormClosingEventArgs e) {
        if (!saved && !discardChanges && e.CloseReason == CloseReason.UserClosing) {
            bool changed = !SameEntries(rootEntries, original);
            if (editingIndex >= 0) changed |= name.Text != entries[editingIndex].Name || (!entries[editingIndex].IsGroup && path.Text != entries[editingIndex].Path);
            if (changed) {
                var answer = MessageBox.Show(this, "快捷项有未保存的修改，是否保存？", "快捷托盘", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (answer == DialogResult.Cancel) e.Cancel = true;
                else if (answer == DialogResult.Yes) {
                    if (!CommitPending()) e.Cancel = true;
                    else try { commit(Snapshot()); saved = true; DeleteDraft(); }
                    catch (Exception ex) { MessageBox.Show(this, ex.Message, "保存失败"); e.Cancel = true; }
                } else DeleteDraft();
            }
        }
        base.OnFormClosing(e);
    }
    Settings Snapshot() {
        var result = new Settings();
        foreach (var entry in rootEntries) result.Entries.Add(entry.Clone());
        return result;
    }
    void DeleteDraft() { try { if (File.Exists(Storage.DraftFile)) File.Delete(Storage.DraftFile); } catch(Exception ex) { Diagnostics.Log(ex); } }
    void SaveDraft() {
        if (!ready || saved || discardChanges) return;
        try {
            if (SameEntries(rootEntries, original)) return;
            var snapshot = Snapshot();
            string data = Storage.Serialize(snapshot); if (data == lastDraft) return;
            Storage.Write(snapshot, Storage.DraftFile); lastDraft = data;
        } catch(Exception ex) { Diagnostics.Log(ex); }
    }
    static bool SameEntries(List<ShortcutEntry> a, List<ShortcutEntry> b) {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++) {
            var x = a[i]; var y = b[i];
            if (x.Name != y.Name || x.Path != y.Path || x.IsGroup != y.IsGroup || x.Arguments != y.Arguments || x.WorkingDirectory != y.WorkingDirectory || !SameEntries(x.Children, y.Children)) return false;
        }
        return true;
    }
    static ShortcutEntry FindParent(List<ShortcutEntry> items, ShortcutEntry target, ShortcutEntry parent) {
        if (target == null) return null;
        foreach (var item in items) {
            if (item == target) return parent;
            if (item.IsGroup) { var found = FindParent(item.Children, target, item); if (found != null) return found; }
        }
        return null;
    }
    string GroupLocation(ShortcutEntry group) { return group == null ? "全部快捷项" : GroupLocation(FindParent(rootEntries, group, null)) + " / " + group.Name; }
    void Navigate(ShortcutEntry group) {
        if (!CommitPending()) return;
        currentGroup = group; entries = group == null ? rootEntries : group.Children;
        editingIndex = -1; RefreshList(entries.Count == 0 ? -1 : 0);
    }
    void NewGroup() {
        var group = new ShortcutEntry { IsGroup = true, Name = "新分组" };
        entries.Add(group); RefreshList(entries.Count - 1); name.Focus(); name.SelectAll();
    }
    static void AddGroupNodes(TreeNode parent, List<ShortcutEntry> items, ShortcutEntry excluded) {
        foreach (var entry in items) if (entry.IsGroup && entry != excluded) {
            var node = parent.Nodes.Add(entry.Name); node.Tag = entry;
            AddGroupNodes(node, entry.Children, excluded);
        }
    }
    void MoveToGroup() {
        int i = list.SelectedIndex; if (i < 0) return;
        var selected = SelectedEntries();
        using (var dialog = new Form { Text = "移动到分组", ClientSize = new Size(420, 420), StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false }) {
            var tree = new TreeView { Dock = DockStyle.Fill, HideSelection = false };
            var root = tree.Nodes.Add("全部快捷项"); AddDestinationNodes(root, rootEntries, selected); root.Expand(); tree.SelectedNode = root;
            var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, FlowDirection = FlowDirection.RightToLeft };
            var ok = new Button { Text = "移动", DialogResult = DialogResult.OK, AutoSize = true };
            var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
            footer.Controls.Add(ok); footer.Controls.Add(cancel); dialog.Controls.Add(tree); dialog.Controls.Add(footer); dialog.AcceptButton = ok; dialog.CancelButton = cancel;
            if (dialog.ShowDialog(this) != DialogResult.OK || tree.SelectedNode == null) return;
            var group = tree.SelectedNode.Tag as ShortcutEntry;
            var destination = group == null ? rootEntries : group.Children;
            if (destination == entries) return;
            try { MoveSelection(selected, destination, destination.Count); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "无法移动"); }
        }
    }
    void Relocate(int i, List<ShortcutEntry> destination) {
        var moving = entries[i]; entries.RemoveAt(i); destination.Add(moving); editingIndex = -1;
        RefreshList(Math.Min(i, entries.Count - 1));
    }
    static void AddDestinationNodes(TreeNode parent, List<ShortcutEntry> items, List<ShortcutEntry> excluded) {
        foreach (var item in items) if (item.IsGroup && !excluded.Contains(item)) {
            var node = parent.Nodes.Add(item.Name); node.Tag = item; AddDestinationNodes(node,item.Children,excluded);
        }
    }
    List<ShortcutEntry> SelectedEntries() { var result = new List<ShortcutEntry>(); foreach(int i in list.SelectedIndices) if (i >= 0 && i < entries.Count) result.Add(entries[i]); return result; }
    void RestoreSelection(List<ShortcutEntry> selected) { list.ClearSelected(); for (int i=0;i<entries.Count;i++) if(selected.Contains(entries[i])) list.SetSelected(i,true); }
    static bool ContainsList(ShortcutEntry entry, List<ShortcutEntry> destination) {
        if (!entry.IsGroup) return false;
        if (entry.Children == destination) return true;
        foreach (var child in entry.Children) if (ContainsList(child,destination)) return true;
        return false;
    }
    void MoveSelection(List<ShortcutEntry> selected, List<ShortcutEntry> destination, int index) {
        if (selected.Count == 0 || selected.Exists(item => !entries.Contains(item))) throw new IOException("拖动的条目已改变，请重新选择。");
        foreach(var item in selected) {
            if (ContainsList(item,destination)) throw new IOException("不能把分组放入自身或其子分组。");
            if (destination != entries && !item.IsGroup && destination.Exists(other => !other.IsGroup && String.Equals(other.Path,item.Path,StringComparison.OrdinalIgnoreCase))) throw new IOException("目标分组中已有相同路径，未移动任何条目。");
        }
        var ordered = entries.FindAll(item => selected.Contains(item));
        index = Math.Max(0,Math.Min(index,destination.Count));
        // Compute the insertion index from the original list, before removing anything.
        if (destination == entries) {
            int requested = index;
            // The caller passes the original insertion boundary; count its surviving predecessors.
            requested = Math.Max(0,Math.Min(requested,entries.Count));
            index = 0; for(int i=0;i<requested;i++) if(!selected.Contains(entries[i])) index++;
        }
        foreach(var item in ordered) entries.Remove(item);
        destination.InsertRange(Math.Min(index,destination.Count),ordered); editingIndex=-1;
        RefreshList(-1);
        if(destination==entries){refreshing=true;try{RestoreSelection(ordered);LoadFields(list.SelectedIndex);}finally{refreshing=false;}}
    }
    void ClearDropMarker() { dropIndex=-1;dropGroup=null;list.Invalidate(); }
    void OnListDragOver(object sender, DragEventArgs e) {
        var payload = e.Data.GetData(DragFormat,false) as SelectionDrag;
        bool internalMove = payload != null && payload.Owner == this;
        if (!internalMove && !e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effect=DragDropEffects.None;ClearDropMarker();return; }
        var point=list.PointToClient(new Point(e.X,e.Y));int hit=list.IndexFromPoint(point);
        dropGroup=null;dropIndex=entries.Count;
        if(hit>=0 && hit<entries.Count){var bounds=list.GetItemRectangle(hit);int margin=Math.Max(4,bounds.Height/4);
            if(entries[hit].IsGroup && point.Y>=bounds.Top+margin && point.Y<bounds.Bottom-margin) dropGroup=entries[hit];
            else dropIndex=hit+(point.Y>=bounds.Top+bounds.Height/2?1:0);
        }
        bool valid = !internalMove || dropGroup==null || !payload.Items.Exists(item=>item==dropGroup || ContainsList(item,dropGroup.Children));
        e.Effect=valid?(internalMove?DragDropEffects.Move:DragDropEffects.Copy):DragDropEffects.None;
        if(unchecked(Environment.TickCount-lastScroll)>100 && list.Items.Count>0){
            if(point.Y<20 && list.TopIndex>0)list.TopIndex--;
            else if(point.Y>list.ClientSize.Height-20 && list.TopIndex<list.Items.Count-1)list.TopIndex++;
            lastScroll=Environment.TickCount;
        }
        list.Invalidate();
    }
    void DropSelection(object sender, DragEventArgs e) {
        try {
            if(!CommitPending())return;
            OnListDragOver(sender,e); if(e.Effect==DragDropEffects.None)return;
            var destination=dropGroup==null?entries:dropGroup.Children;
            var payload=e.Data.GetData(DragFormat,false) as SelectionDrag;
            if(payload!=null && payload.Owner==this)MoveSelection(payload.Items,destination,dropGroup==null?dropIndex:destination.Count);
            else {
                var files=e.Data.GetData(DataFormats.FileDrop) as string[];
                if(files!=null){if(dropGroup!=null)Navigate(dropGroup);AddMany(files);}
            }
        }catch(Exception ex){MessageBox.Show(this,ex.Message,"无法移动");}
        finally{ClearDropMarker();}
    }
    void DrawRow(object sender, DrawItemEventArgs e) {
        if(e.Index<0 || e.Index>=list.Items.Count)return;
        bool target=dropGroup!=null && e.Index<entries.Count && entries[e.Index]==dropGroup;
        if(target){using(var brush=new SolidBrush(Color.FromArgb(212,238,218)))e.Graphics.FillRectangle(brush,e.Bounds);}else e.DrawBackground();
        Color color=target?Color.FromArgb(24,95,49):((e.State&DrawItemState.Selected)!=0?SystemColors.HighlightText:list.ForeColor);
        TextRenderer.DrawText(e.Graphics,list.Items[e.Index].ToString(),list.Font,new Rectangle(e.Bounds.X+4,e.Bounds.Y,e.Bounds.Width-8,e.Bounds.Height),color,TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);
        if(dropGroup==null && dropIndex>=0 && (dropIndex==e.Index || (dropIndex==entries.Count && e.Index==entries.Count-1))){
            int y=dropIndex==e.Index?e.Bounds.Top:e.Bounds.Bottom-2;using(var pen=new Pen(Color.DodgerBlue,2))e.Graphics.DrawLine(pen,e.Bounds.Left,y,e.Bounds.Right,y);
        }
        e.DrawFocusRectangle();
    }
    void RefreshList(int selection) {
        list.BeginUpdate();
        refreshing = true;
        try { list.Items.Clear(); list.Items.AddRange(entries.ToArray()); list.SelectedIndex = selection; LoadFields(selection); location.Text = "当前位置：" + GroupLocation(currentGroup) + "　（Ctrl 多选；拖到分组中间归类，拖到条目边缘排序）"; }
        finally { refreshing = false; list.EndUpdate(); }
        SaveDraft();
    }
    protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing) uiFont.Dispose(); }
}
static class Program {
    [STAThread]
    static int Main(string[] args) {
        if (args.Length > 0 && args[0] == "--self-test") return SelfTest(args.Length > 1 ? args[1] : System.IO.Path.GetTempPath());
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e) {
            Diagnostics.Log(e.Exception);
            MessageBox.Show("操作发生异常，已记录日志。未保存的有效修改可在重新打开管理界面时恢复。\n\n" + e.Exception.Message, "快捷托盘", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };
        AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e) { Diagnostics.Log(e.ExceptionObject as Exception ?? new Exception("Unknown fatal error")); };
        bool first;
        using (var mutex = new Mutex(true, "Local\\QuickShelf", out first)) {
            if (!first) { MessageBox.Show("快捷托盘已经运行，请查看右下角托盘或隐藏图标区域。", "快捷托盘"); return 0; }
            try { Application.Run(new Shelf()); }
            catch (Exception ex) { Diagnostics.Log(ex); MessageBox.Show("启动失败。原配置不会被覆盖。\n\n" + ex.Message, "快捷托盘", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
        }
        return 0;
    }
    static int SelfTest(string directory) {
        try {
            string test = System.IO.Path.Combine(directory, "quickshelf-test-" + Guid.NewGuid().ToString("N") + ".json");
            var settings = new Settings(); settings.Entries.Add(new ShortcutEntry { Name = "中文 & <名称>", Path = @"D:\有 空格\file.txt" });
            Storage.Write(settings, test); var loaded = Storage.Read(test);
            if (loaded.Entries.Count != 1 || loaded.Entries[0].Name != settings.Entries[0].Name || loaded.Entries[0].Path != settings.Entries[0].Path) throw new Exception("JSON round-trip failed");
            loaded.Entries.Clear(); Storage.Write(loaded, test);
            if (Storage.Read(test).Entries.Count != 0 || Storage.Read(test + ".bak").Entries.Count != 1) throw new Exception("Atomic backup failed");
            if (Launcher.LocationArguments(@"D:\有 空格\file.txt") != "/select,\"D:\\有 空格\\file.txt\"") throw new Exception("Explorer quoting failed");
            File.Delete(test); File.Delete(test + ".bak");
            File.WriteAllText(System.IO.Path.Combine(directory, "self-test-result.txt"), "PASS: Unicode settings, atomic backup, empty list, Explorer argument quoting."); return 0;
        } catch (Exception ex) { File.WriteAllText(System.IO.Path.Combine(directory, "self-test-result.txt"), ex.ToString()); return 1; }
    }
}
