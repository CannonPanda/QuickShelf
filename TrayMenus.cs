using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

sealed class ShelfHotKey : NativeWindow, IDisposable {
    const int HotKeyId = 0x5153;
    readonly Action<IntPtr> show;
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    static extern bool UnregisterHotKey(IntPtr window, int id);
    public ShelfHotKey(Action<IntPtr> callback) {
        show = callback;
        CreateHandle(new CreateParams());
        if (!RegisterHotKey(Handle, HotKeyId, 0x4000, (uint)Keys.F4)) {
            int error = Marshal.GetLastWin32Error();
            DestroyHandle();
            throw new System.ComponentModel.Win32Exception(error, "无法注册 F4 快捷键，可能已被其他程序占用。");
        }
    }
    protected override void WndProc(ref Message message) {
        if (message.Msg == 0x312 && message.WParam.ToInt32() == HotKeyId) {
            try { show(Handle); } catch (Exception ex) { Diagnostics.Log(ex); }
            return;
        }
        base.WndProc(ref message);
    }
    public void Dispose() {
        if (Handle == IntPtr.Zero) return;
        UnregisterHotKey(Handle, HotKeyId); DestroyHandle();
    }
}

sealed class PathMenuItem : ToolStripMenuItem {
    public Rectangle CopyBounds { get; set; }
    public Rectangle FavoriteBounds { get; set; }
    public PathMenuItem(string text) : base(text) { }
}

sealed class CopyMenuRenderer : ToolStripProfessionalRenderer {
    protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e) {
        base.OnRenderItemImage(e);
        var item = e.Item as PathMenuItem;
        if (item != null) {
            var bounds = e.ImageRectangle;
            int half = bounds.Width / 2;
            item.CopyBounds = new Rectangle(bounds.X, bounds.Y - 3, half, bounds.Height + 6);
            item.FavoriteBounds = new Rectangle(bounds.X + half, bounds.Y - 3, bounds.Width - half, bounds.Height + 6);
        }
    }
}

sealed class Shelf : ApplicationContext, IMessageFilter {
    readonly NotifyIcon tray;
    readonly Icon trayIcon;
    readonly Bitmap copyImage = CreateCopyImage();
    readonly Bitmap savedImage = CreateCopyImage(true);
    readonly CopyMenuRenderer renderer = new CopyMenuRenderer();
    readonly ContextMenuStrip menu = new ContextMenuStrip();
    readonly ShelfHotKey hotKey;
    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")]
    static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    Settings settings;
    Manager manager;
    PathMenuItem pressedCopy;
    bool pressedFavorite;
    bool exiting;
    static readonly MethodInfo ShowContextMenu = typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic);

    public Shelf() {
        settings = Storage.Read(Storage.FileName);
        ConfigureMenu(menu);
        Application.AddMessageFilter(this);
        menu.Closed += delegate {
            pressedCopy = null;
            if (!exiting && menu.IsHandleCreated) menu.BeginInvoke((MethodInvoker)delegate {
                if (exiting || menu.Visible) return;
                ReleaseExpandedMenus();
            });
        };
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("QuickShelf.Icon"))
        using (var icon = new Icon(stream, SystemInformation.SmallIconSize)) trayIcon = (Icon)icon.Clone();
        // No permanent ContextMenuStrip: NotifyIcon must not open anything on right-click.
        tray = new NotifyIcon { Icon = trayIcon, Text = "", Visible = true };
        tray.MouseClick += delegate(object s, MouseEventArgs e) {
            if (e.Button != MouseButtons.Left) return;
            // Use WinForms' foreground-window handling, then detach before the next input.
            tray.ContextMenuStrip = menu;
            try { ShowContextMenu.Invoke(tray, null); }
            finally { tray.ContextMenuStrip = null; }
        };
        EnsureStartup();
        Rebuild(); if (settings.Entries.Count == 0) ShowManager();
        try { hotKey = new ShelfHotKey(ShowAtCursor); }
        catch (System.ComponentModel.Win32Exception ex) { MessageBox.Show(ex.Message, "快捷托盘", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
    void ShowAtCursor(IntPtr window) {
        if (exiting) return;
        Point position = Cursor.Position;
        menu.Close();
        SetForegroundWindow(window);
        menu.Show(position);
        PostMessage(window, 0, IntPtr.Zero, IntPtr.Zero);
    }
    static Bitmap CreateCopyImage() {
        return CreateCopyImage(false);
    }
    static Bitmap CreateCopyImage(bool saved) {
        var image = new Bitmap(36, 16);
        using (var graphics = Graphics.FromImage(image))
        using (var pen = new Pen(Color.FromArgb(65, 100, 140))) {
            graphics.DrawLines(pen, new Point[] { new Point(4, 11), new Point(2, 11), new Point(2, 2), new Point(10, 2), new Point(10, 4) });
            graphics.DrawRectangle(pen, 5, 5, 8, 9);
            var points = new PointF[10];
            for (int i = 0; i < points.Length; i++) {
                double angle = -Math.PI / 2 + i * Math.PI / 5;
                float radius = i % 2 == 0 ? 7F : 3F;
                points[i] = new PointF(27F + radius * (float)Math.Cos(angle), 8F + radius * (float)Math.Sin(angle));
            }
            if (saved) using (var fill = new SolidBrush(Color.FromArgb(245, 181, 35))) graphics.FillPolygon(fill, points);
            using (var star = new Pen(Color.FromArgb(172, 114, 10))) graphics.DrawPolygon(star, points);
        }
        return image;
    }
    static void ClearItems(ToolStripItemCollection items) {
        while (items.Count > 0) { int index = items.Count - 1; var old = items[index]; items.RemoveAt(index); old.Dispose(); }
    }
    void ReleaseExpandedMenus() {
        if (exiting || menu.IsDisposed || menu.Visible) return;
        foreach (ToolStripItem entry in menu.Items) {
            var item = entry as ToolStripMenuItem;
            if (item == null || !(item.Tag is ShortcutEntry) || !item.HasDropDownItems) continue;
            item.DropDown.SuspendLayout();
            try { ClearItems(item.DropDownItems); item.DropDownItems.Add(new ToolStripMenuItem("正在读取…") { Enabled = false }); }
            finally { item.DropDown.ResumeLayout(false); }
        }
    }
    void ConfigureMenu(ToolStripDropDown dropdown) {
        dropdown.ShowItemToolTips = false;
        dropdown.Renderer = renderer;
    }
    bool OwnsMenu(ToolStripDropDown dropdown) {
        while (dropdown != null) {
            if (dropdown == menu) return true;
            dropdown = dropdown.OwnerItem == null ? null : dropdown.OwnerItem.Owner as ToolStripDropDown;
        }
        return false;
    }
    public bool PreFilterMessage(ref Message message) {
        if (message.Msg != 0x201 && message.Msg != 0x202 && message.Msg != 0x203 &&
            message.Msg != 0x204 && message.Msg != 0x205 && message.Msg != 0x206 && message.Msg != 0x7B) return false;
        var dropdown = Control.FromHandle(message.HWnd) as ToolStripDropDown;
        Point point = dropdown == null ? Point.Empty : dropdown.PointToClient(Cursor.Position);
        return HandleMenuMouse(message.Msg, dropdown, point);
    }
    bool HandleMenuMouse(int message, ToolStripDropDown dropdown, Point point) {
        bool owned = OwnsMenu(dropdown);
        var item = owned ? dropdown.GetItemAt(point) as PathMenuItem : null;
        bool onCopy = item != null && item.CopyBounds.Contains(new Point(point.X - item.Bounds.X, point.Y - item.Bounds.Y));
        bool onFavorite = item != null && item.FavoriteBounds.Contains(new Point(point.X - item.Bounds.X, point.Y - item.Bounds.Y));
        if (message == 0x202 && pressedCopy != null) {
            var pressed = pressedCopy; pressedCopy = null;
            if (item == pressed) {
                if (pressedFavorite && onFavorite) FavoritePath((ShortcutEntry)item.Tag);
                else if (!pressedFavorite && onCopy) CopyPath((ShortcutEntry)item.Tag);
            }
            return true;
        }
        if (!owned) return false;
        // Ignore secondary mouse/context-menu input throughout this menu hierarchy.
        if (message == 0x204 || message == 0x205 || message == 0x206 || message == 0x7B) return true;
        if ((message == 0x201 || message == 0x203) && (onCopy || onFavorite)) { pressedCopy = item; pressedFavorite = onFavorite; return true; }
        return false;
    }
    void CopyPath(ShortcutEntry entry) {
        try {
            Clipboard.SetText(Path.GetFullPath(entry.Path));
            menu.Close();
        } catch (Exception ex) {
            menu.Close(); MessageBox.Show(ex.Message, "无法复制路径", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
    void FavoritePath(ShortcutEntry entry) {
        if (manager != null && !manager.IsDisposed) {
            MessageBox.Show("请先保存或取消管理界面的修改，再收藏路径。", "快捷托盘", MessageBoxButtons.OK, MessageBoxIcon.Information); return;
        }
        try {
            settings = Favorites.Toggle(entry);
            RefreshFavoriteIcons(menu);
            if (menu.IsHandleCreated) menu.BeginInvoke((MethodInvoker)delegate {
                if (!exiting && !menu.IsDisposed) SyncSavedMenus(menu, settings.Entries, true);
            });
        } catch (Exception ex) { Diagnostics.Log(ex); MessageBox.Show(ex.Message, "收藏失败", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
    static bool SameMenuEntry(ShortcutEntry a, ShortcutEntry b) {
        return a.IsGroup == b.IsGroup && (a.IsGroup ? a.Name == b.Name : String.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
    }
    void SyncSavedMenus(ToolStripDropDown dropdown, List<ShortcutEntry> entries, bool root) {
        var pool = new List<ToolStripMenuItem>();
        foreach (ToolStripItem child in dropdown.Items) {
            var item = child as ToolStripMenuItem;
            if (item != null && item.Tag is ShortcutEntry) pool.Add(item);
        }
        var desired = new List<ToolStripMenuItem>();
        foreach (var entry in entries) {
            var item = pool.Find(candidate => SameMenuEntry((ShortcutEntry)candidate.Tag, entry));
            if (item == null) item = EntryItem(entry, entry.IsGroup || Directory.Exists(entry.Path));
            else { pool.Remove(item); item.Tag = entry; }
            desired.Add(item);
        }
        dropdown.SuspendLayout();
        try {
            foreach (var item in pool) { dropdown.Items.Remove(item); item.Dispose(); }
            // Remove placeholders, while preserving root-level management commands.
            for (int i = dropdown.Items.Count - 1; i >= 0; i--) {
                var item = dropdown.Items[i];
                if (!(item.Tag is ShortcutEntry) && (!root || !item.Enabled && item is ToolStripMenuItem)) { dropdown.Items.RemoveAt(i); item.Dispose(); }
            }
            for (int i = 0; i < desired.Count; i++) {
                var item = desired[i];
                if (dropdown.Items.IndexOf(item) != i) { dropdown.Items.Remove(item); dropdown.Items.Insert(i, item); }
                var entry = (ShortcutEntry)item.Tag;
                if (entry.IsGroup && item.DropDown.Visible) SyncSavedMenus(item.DropDown, entry.Children, false);
            }
            if (entries.Count == 0) dropdown.Items.Insert(0, new ToolStripMenuItem(root ? "尚未添加快捷项" : "（空分组）") { Enabled = false });
        } finally { dropdown.ResumeLayout(true); }
        RefreshFavoriteIcons(dropdown);
    }
    void RefreshFavoriteIcons(ToolStripDropDown dropdown) {
        foreach (ToolStripItem child in dropdown.Items) {
            var pathItem = child as PathMenuItem;
            if (pathItem != null) pathItem.Image = Favorites.IsSaved(settings, (ShortcutEntry)pathItem.Tag) ? savedImage : copyImage;
            var item = child as ToolStripMenuItem;
            if (item != null && item.HasDropDownItems) RefreshFavoriteIcons(item.DropDown);
        }
        dropdown.Invalidate();
    }
    void HoverEntry(ToolStripMenuItem item, bool folder) {
        if (exiting || item.IsDisposed || item.Owner == null || !item.Owner.Visible) return;
        foreach (ToolStripItem sibling in item.Owner.Items) {
            var other = sibling as ToolStripMenuItem;
            if (other != null && other != item && other.HasDropDownItems && other.DropDown.Visible) other.HideDropDown();
        }
        if (folder && !item.DropDown.Visible) item.ShowDropDown();
    }
    ToolStripMenuItem EntryItem(ShortcutEntry entry, bool folder) {
        folder = folder || entry.IsGroup;
        ToolStripMenuItem item = entry.IsGroup ? new ToolStripMenuItem(entry.Name.Replace("&", "&&")) : new PathMenuItem(entry.Name.Replace("&", "&&")) { Image = Favorites.IsSaved(settings, entry) ? savedImage : copyImage, ImageScaling = ToolStripItemImageScaling.None };
        item.AutoToolTip = false; item.ToolTipText = ""; item.Tag = entry;
        if (!entry.IsGroup) item.Click += delegate { menu.Close(); Launcher.Open((ShortcutEntry)item.Tag, false); };
        item.MouseEnter += delegate { HoverEntry(item, folder); };
        if (folder) {
            ConfigureMenu(item.DropDown); item.DropDownItems.Add(new ToolStripMenuItem("正在读取…") { Enabled = false });
            item.DropDownOpening += delegate {
                var current = (ShortcutEntry)item.Tag;
                item.DropDown.SuspendLayout();
                try {
                    ClearItems(item.DropDownItems);
                    try {
                        if (current.IsGroup) {
                            foreach (var child in current.Children) item.DropDownItems.Add(EntryItem(child, child.IsGroup || Directory.Exists(child.Path)));
                            if (current.Children.Count == 0) item.DropDownItems.Add(new ToolStripMenuItem("（空分组）") { Enabled = false });
                        } else {
                            var children = FolderBrowser.Read(current.Path);
                            foreach (var child in children) item.DropDownItems.Add(EntryItem(child.Entry, child.IsDirectory));
                            if (children.Count == 0) item.DropDownItems.Add(new ToolStripMenuItem("（空文件夹）") { Enabled = false });
                        }
                    } catch (Exception ex) { item.DropDownItems.Add(new ToolStripMenuItem("无法读取目录：" + ex.Message.Replace("&", "&&")) { Enabled = false }); }
                } finally { item.DropDown.ResumeLayout(true); }
            };
        }
        return item;
    }

    void EnsureStartup() {
        try {
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                key.SetValue("QuickShelf", "\"" + Application.ExecutablePath + "\"");
        } catch (Exception) { }
    }
    void Rebuild() {
        menu.SuspendLayout();
        try {
        ClearItems(menu.Items);
        if (settings.Entries.Count == 0) menu.Items.Add(new ToolStripMenuItem("尚未添加快捷项") { Enabled = false });
        foreach (var entry in settings.Entries) menu.Items.Add(EntryItem(entry, Directory.Exists(entry.Path)));
        menu.Items.Add(new ToolStripSeparator()); menu.Items.Add("管理快捷项…", null, delegate { ShowManager(); });
        } finally { menu.ResumeLayout(true); }
    }
    void ShowManager() {
        if (manager != null && !manager.IsDisposed) { manager.Show(); manager.Activate(); return; }
        try {
            string revision = Storage.Fingerprint(Storage.FileName);
            var baseline = Storage.Read(Storage.FileName);
            var editing = baseline;
            if (File.Exists(Storage.DraftFile)) {
                if (MessageBox.Show("发现上次未保存的快捷项修改，是否恢复？", "恢复快捷项", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) editing = Storage.Read(Storage.DraftFile);
                else File.Delete(Storage.DraftFile);
            }
            manager = new Manager(editing, delegate(Settings updated) {
                revision = Storage.WriteChecked(updated, Storage.FileName, revision);
                settings = updated;
                if (menu.IsHandleCreated) menu.BeginInvoke((MethodInvoker)delegate { if (!exiting) Rebuild(); });
                else Rebuild();
            }, baseline); manager.Show();
        } catch (Exception ex) { Diagnostics.Log(ex); MessageBox.Show(ex.Message, "无法加载快捷项", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
    protected override void ExitThreadCore() {
        if (exiting) return;
        exiting = true;
        if (hotKey != null) hotKey.Dispose();
        Application.RemoveMessageFilter(this); if (manager != null) manager.Dispose();
        tray.Visible = false; tray.Dispose(); trayIcon.Dispose(); menu.Dispose(); copyImage.Dispose(); savedImage.Dispose(); base.ExitThreadCore();
    }
}


