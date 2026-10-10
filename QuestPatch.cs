using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Drawing;
using System.Diagnostics;
using System.Reflection;

public class Op { public int offset; public int remove; public string data; public int any; }
public class Variant { public Op[] ops; public string hash; }
public class FileRecipe { public string path,hash; public int length,mask; public Op[] ops; public Dictionary<string,Variant> variants; }
public class Legacy { public string path,hash; public Op[] ops; }
public class Feature { public string id,label; public int bit; }
public class Payload { public string version,backupFolder; public Feature[] features; public FileRecipe[] files; public Legacy[] legacy; public Payload[] profiles; }
public class State { public int format=1; public int mask; public bool imported; public Dictionary<string,string> current,initial; }

public class Engine {
    public static JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength=16000000 };
    public Payload P; public Payload[] Profiles;
    public Engine() { using(var s=Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.json")) using(var r=new StreamReader(s,Encoding.UTF8)) {var root=Json.Deserialize<Payload>(r.ReadToEnd());Profiles=root.profiles??new[]{root};P=Profiles.Last();} }
    public void Select(string root) {
        string path=FilePath(root,"TF2VR/plugins/Titanfall2VR.dll");
        if(!File.Exists(path))throw new Exception("Не найдена DLL VR-мода. Выберите папку установленной игры Titanfall2.");
        string hash=Hash(File.ReadAllBytes(path));
        foreach(var profile in Profiles) {
            if(profile.files[0].hash==hash || (profile.legacy??new Legacy[0]).Any(f=>f.path.EndsWith(".dll")&&f.hash==hash)){P=profile;return;}
            string journal=FilePath(root,(profile.backupFolder??".quest-custom-patch")+"/state.json");
            if(File.Exists(journal)) {var state=Json.Deserialize<State>(File.ReadAllText(journal));string current;if(state!=null&&state.current!=null&&state.current.TryGetValue(profile.files[0].path,out current)&&current==hash){P=profile;return;}}
        }
        foreach(var profile in Profiles.Reverse())if(Directory.Exists(FilePath(root,(profile.backupFolder??".quest-custom-patch")+"/pending"))){P=profile;return;}
        foreach(var profile in Profiles.Reverse())if(File.Exists(FilePath(root,(profile.backupFolder??".quest-custom-patch")+"/state.json"))){P=profile;return;}
        P=Profiles.Last();
    }
    public static string Hash(byte[] b) { using(var s=SHA256.Create()) return BitConverter.ToString(s.ComputeHash(b)).Replace("-","").ToLowerInvariant(); }
    public string Backup(string root) { return Path.Combine(Path.GetFullPath(root),P.backupFolder??".quest-custom-patch"); }
    string BackupRel(string suffix="") {return (P.backupFolder??".quest-custom-patch")+(suffix.Length>0?"/"+suffix:"");}
    string FilePath(string root,string rel) {
        string full=Path.GetFullPath(Path.Combine(root,rel.Replace('/',Path.DirectorySeparatorChar)));
        if(!full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) throw new Exception("Недопустимый путь.");
        // Never follow junctions/symlinks out of the selected installation.
        for(string t=full; t!=null; t=Path.GetDirectoryName(t)) if((File.Exists(t)||Directory.Exists(t)) && (File.GetAttributes(t)&FileAttributes.ReparsePoint)!=0) throw new Exception("Путь содержит ссылку или junction: "+t);
        return full;
    }
    public static byte[] Edit(byte[] source,Op[] ops) {
        using(var m=new MemoryStream()) { int pos=0;
            foreach(var o in ops.OrderBy(x=>x.offset)) {
                if(o.offset<pos||o.offset>source.Length||o.remove<0||o.offset+o.remove>source.Length) throw new Exception("Повреждён рецепт патча.");
                m.Write(source,pos,o.offset-pos); byte[] b=Convert.FromBase64String(o.data);m.Write(b,0,b.Length);pos=o.offset+o.remove;
            }
            m.Write(source,pos,source.Length-pos);return m.ToArray();
        }
    }
    public byte[] Build(FileRecipe f,byte[] source,int mask) {
        if(Hash(source)!=f.hash) throw new Exception("Повреждена исходная резервная копия: "+f.path);
        if(mask==0) return source;
        if(f.variants!=null) {
            var v=f.variants[(mask&f.mask).ToString()];var b=Edit(source,v.ops);
            if(Hash(b)!=v.hash) throw new Exception("Ошибка проверки скрипта.");return b;
        }
        var dst=new List<byte>(source);
        foreach(var o in f.ops) if((mask&o.any)!=0) {
            var b=Convert.FromBase64String(o.data);
            if(o.offset<0||o.offset>dst.Count) throw new Exception("Ошибка размещения DLL.");
            for(int i=0;i<b.Length;i++) if(o.offset+i<dst.Count) dst[o.offset+i]=b[i];else dst.Add(b[i]);
        }
        return dst.ToArray();
    }
    State LoadState(string root) {
        string path=FilePath(root,BackupRel("state.json"));
        if(!File.Exists(path)) return null;
        var state=Json.Deserialize<State>(File.ReadAllText(path));
        if(state==null||state.format!=1||state.current==null||state.initial==null)throw new Exception("Повреждён журнал патча.");
        return state;
    }
    public State Check(string root) {
        Select(root);
        if(Directory.Exists(FilePath(root,BackupRel("pending")))) throw new Exception("Незавершённая установка. Нажмите «Восстановить» для возврата файлов.");
        var s=LoadState(root); bool stock=true,legacy=true;
        foreach(var f in P.files) {
            string path=FilePath(root,f.path);
            if(!File.Exists(path)) throw new Exception("Не найден файл: "+f.path+"\nСначала установите VR-мод через CircuitLordVRModInstaller.");
            string h=Hash(File.ReadAllBytes(path));
            if(s!=null) { if(!s.current.ContainsKey(f.path)||h!=s.current[f.path]) throw new Exception("Файл изменён после установки патча: "+f.path+"\nВозможно, VR-мод обновился. Запись остановлена; резервные копии сохранены."); }
            stock &= h==f.hash;var prior=(P.legacy??new Legacy[0]).FirstOrDefault(x=>x.path==f.path);legacy &= prior!=null&&h==prior.hash;
        }
        if(s!=null) {
            foreach(var f in P.files) if(Hash(File.ReadAllBytes(FilePath(root,BackupRel("baseline/"+f.path))))!=f.hash) throw new Exception("Повреждена резервная копия.");
            return s;
        }
        if(!stock&&!legacy) throw new Exception("Эта сборка VR-мода не поддерживается. Требуются проверенные исходные файлы Titanfall 2 VR 1.0.13 или 1.0.14.\nПатч не изменяет неизвестные DLL и скрипты.");
        return new State { mask=legacy?511:0,imported=legacy,current=P.files.ToDictionary(x=>x.path,x=>Hash(File.ReadAllBytes(FilePath(root,x.path)))) };
    }
    void ClosedGame() {
        foreach(var p in Process.GetProcesses()) { try { string n=p.ProcessName.ToLowerInvariant(); if(n=="titanfall2"||n=="northstar"||n=="titanfall2vr")throw new InvalidOperationException("Закройте игру перед изменением файлов."); } finally { p.Dispose(); } }
    }
    public void Apply(string root,int mask) {
        ClosedGame();root=Path.GetFullPath(root);
        // Reload on Trigger must free the same Trigger's pilot ability binding.
        if((mask&1)!=0 && (mask&2)==0) throw new Exception("Для перезарядки на левом Trigger включите перенос способности пилота на L3.");
        if(mask<0||mask>511)throw new Exception("Неверный набор функций.");
        var s=Check(root);
        string dir=FilePath(root,BackupRel());Directory.CreateDirectory(dir);
        using(var gate=new FileStream(FilePath(root,BackupRel("lock")),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None)) {
            s=Check(root);
            var bases=new Dictionary<string,byte[]>();
            foreach(var f in P.files) {
                string bp=FilePath(root,BackupRel("baseline/"+f.path));
                byte[] b;
                if(LoadState(root)==null) {
                    b=File.ReadAllBytes(FilePath(root,f.path));
                    var initialPath=FilePath(root,BackupRel("initial/"+f.path));Directory.CreateDirectory(Path.GetDirectoryName(initialPath));
                    if(File.Exists(initialPath) && Hash(File.ReadAllBytes(initialPath))!=s.current[f.path])throw new Exception("Обнаружена предыдущая резервная копия. Не удаляйте её; установка остановлена.");
                    if(!File.Exists(initialPath))File.WriteAllBytes(initialPath,b);
                    if(s.imported)b=Edit(b,P.legacy.First(x=>x.path==f.path).ops);
                    if(Hash(b)!=f.hash)throw new Exception("Не удалось восстановить исходную версию.");
                    Directory.CreateDirectory(Path.GetDirectoryName(bp));
                    if(File.Exists(bp)&&Hash(File.ReadAllBytes(bp))!=f.hash)throw new Exception("Резервная копия повреждена.");
                    if(!File.Exists(bp))File.WriteAllBytes(bp,b);
                } else b=File.ReadAllBytes(bp);
                bases[f.path]=b;
            }
            if(s.initial==null)s.initial=new Dictionary<string,string>(s.current);
            var output=P.files.ToDictionary(f=>f.path,f=>Build(f,bases[f.path],mask));
            s.mask=mask;s.current=output.ToDictionary(x=>x.Key,x=>Hash(x.Value));
            output[BackupRel("state.json")]=Encoding.UTF8.GetBytes(Json.Serialize(s));
            Transaction(root,output);
        }
    }
    static void Atomic(string path,byte[] bytes) {
        string tmp=path+".quest-"+Guid.NewGuid().ToString("N")+".tmp";
        try { using(var s=new FileStream(tmp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){s.Write(bytes,0,bytes.Length);s.Flush(true);} if(File.Exists(path))File.Replace(tmp,path,null);else File.Move(tmp,path); }
        finally { if(File.Exists(tmp))File.Delete(tmp); }
    }
    void Transaction(string root,Dictionary<string,byte[]> output) {
        string pending=FilePath(root,BackupRel("pending"));Directory.CreateDirectory(pending);
        var previous=new Dictionary<string,string>();
        foreach(var pair in output) {
            string path=FilePath(root,pair.Key);previous[pair.Key]=File.Exists(path)?Convert.ToBase64String(File.ReadAllBytes(path)):null;
        }
        Atomic(Path.Combine(pending,"journal.json"),Encoding.UTF8.GetBytes(Json.Serialize(previous)));
        try {
            foreach(var pair in output) {
                string path=FilePath(root,pair.Key);Atomic(path,pair.Value);
                if(Hash(File.ReadAllBytes(path))!=Hash(pair.Value))throw new Exception("Не удалось проверить записанный файл.");
            }
            Directory.Delete(pending,true);
        } catch { Recover(root);throw; }
    }
    void Recover(string root) {
        string pending=FilePath(root,BackupRel("pending"));string journal=Path.Combine(pending,"journal.json");
        if(!File.Exists(journal))throw new Exception("Отсутствует журнал восстановления. Файлы не перезаписаны.");
        var before=Json.Deserialize<Dictionary<string,string>>(File.ReadAllText(journal));
        var allowed=new HashSet<string>(P.files.Select(f=>f.path));allowed.Add(BackupRel("state.json"));
        if(before.Count!=allowed.Count||before.Keys.Any(k=>!allowed.Contains(k)))throw new Exception("Некорректный журнал восстановления.");
        foreach(var p in before) { string path=FilePath(root,p.Key);if(p.Value==null){if(File.Exists(path))File.Delete(path);}else Atomic(path,Convert.FromBase64String(p.Value)); }
        Directory.Delete(pending,true);
    }
    public void Restore(string root) {
        ClosedGame(); Select(root);string dir=FilePath(root,BackupRel());
        if(!Directory.Exists(dir))throw new Exception("Резервных копий этого патча ещё нет.");
        using(var gate=new FileStream(FilePath(root,BackupRel("lock")),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None)) {
            if(Directory.Exists(FilePath(root,BackupRel("pending")))){Recover(root);return;}
            var s=Check(root);if(s.initial==null)throw new Exception("Нет установленного патча.");
            var output=new Dictionary<string,byte[]>();
            foreach(var f in P.files) {var b=File.ReadAllBytes(FilePath(root,BackupRel("initial/"+f.path)));if(Hash(b)!=s.initial[f.path])throw new Exception("Повреждена исходная копия.");output[f.path]=b;}
            s.current=new Dictionary<string,string>(s.initial);s.mask=s.imported?511:0;
            output[BackupRel("state.json")]=Encoding.UTF8.GetBytes(Json.Serialize(s));Transaction(root,output);
        }
    }
}

public class MainWindow:Form {
    Engine engine=new Engine();TextBox folder=new TextBox();CheckedListBox options=new CheckedListBox();Label status=new Label();Button apply=new Button(),restore=new Button(); bool updating;
    public MainWindow() {
        Text="Titanfall 2 VR • Quest Custom Patch";ClientSize=new Size(740,580);MinimumSize=MaximumSize=Size;FormBorderStyle=FormBorderStyle.FixedSingle;MaximizeBox=false;StartPosition=FormStartPosition.CenterScreen;Font=new Font("Segoe UI",10);BackColor=Color.FromArgb(245,247,250);
        Controls.Add(new Label{Text="Titanfall 2 VR — удобное управление",Font=new Font("Segoe UI",17,FontStyle.Bold),Location=new Point(22,18),Size=new Size(690,40)});
        Controls.Add(new Label{Text="Патчер 1.1.1 • VR-мод 1.0.13 / 1.0.14 • Quest 3 / Touch",Location=new Point(24,64),Size=new Size(690,25)});
        folder.SetBounds(24,105,577,28);folder.Text=Directory.Exists(@"D:\SteamLibrary\steamapps\common\Titanfall2")?@"D:\SteamLibrary\steamapps\common\Titanfall2":"";Controls.Add(folder);
        var browse=new Button{Text="Папка игры…",Location=new Point(610,103),Size=new Size(106,31)};browse.Click+=(s,e)=>{using(var d=new FolderBrowserDialog{Description="Выберите папку Titanfall2",SelectedPath=folder.Text})if(d.ShowDialog()==DialogResult.OK){folder.Text=d.SelectedPath;Inspect();}};Controls.Add(browse);
        options.SetBounds(24,150,692,245);options.CheckOnClick=true;options.BorderStyle=BorderStyle.FixedSingle;options.BackColor=Color.White;
        foreach(var f in engine.P.features)options.Items.Add(f.label,true);
        options.ItemCheck+=(s,e)=>{if(updating)return;updating=true;try {if(e.Index==0&&e.NewValue==CheckState.Checked)options.SetItemChecked(1,true);if(e.Index==1&&e.NewValue==CheckState.Unchecked)options.SetItemChecked(0,false);}finally{updating=false;}};
        Controls.Add(options);
        Controls.Add(new Label{Text="Титан: Grip + Trigger — способность 1; + A — 2; + B — 3; + R3 — ядро.\nОбмен на B: оба оружия убраны, руки пустые, другие кнопки отпущены.\nРезервные копии сохраняются в папке игры. Перед применением закройте игру.",Location=new Point(24,404),Size=new Size(692,66)});
        status.SetBounds(24,475,692,45);status.ForeColor=Color.FromArgb(60,80,100);Controls.Add(status);
        apply.Text="Применить выбранное";apply.SetBounds(24,528,220,34);apply.Click+=(s,e)=>Run(()=>{int mask=0;for(int i=0;i<options.Items.Count;i++)if(options.GetItemChecked(i))mask|=engine.P.features[i].bit;engine.Apply(folder.Text,mask);status.Text="Готово. Выбранные изменения установлены; файлы проверены.";});Controls.Add(apply);
        restore.Text="Восстановить";restore.SetBounds(256,528,160,34);restore.Click+=(s,e)=>Run(()=>{engine.Restore(folder.Text);Inspect();status.Text="Восстановлены файлы до первого применения этого патча.";});Controls.Add(restore);
        var check=new Button{Text="Проверить файлы",Location=new Point(430,528),Size=new Size(170,34)};check.Click+=(s,e)=>Inspect();Controls.Add(check);
        Shown+=(s,e)=>Inspect();
    }
    void Run(Action action) {try{apply.Enabled=restore.Enabled=false;UseWaitCursor=true;action();}catch(Exception ex){status.Text="Изменения не применены.";MessageBox.Show(this,ex.Message,"Quest Custom Patch",MessageBoxButtons.OK,MessageBoxIcon.Warning);}finally{UseWaitCursor=false;apply.Enabled=restore.Enabled=true;}}
    void Inspect() {try{var s=engine.Check(folder.Text);bool fresh=s.mask==0&&!File.Exists(Path.Combine(engine.Backup(folder.Text),"state.json"));updating=true;for(int i=0;i<options.Items.Count;i++)options.SetItemChecked(i,fresh||(s.mask&engine.P.features[i].bit)!=0);updating=false;status.Text="Найдена VR "+engine.P.version+". "+(s.imported?"Импорт предыдущих изменений доступен.":"Выберите нужные изменения.");apply.Enabled=true;}catch(Exception ex){updating=false;status.Text=ex.Message.Split('\n')[0];apply.Enabled=false;}}
}

public static class Program {
    [STAThread] public static int Main(string[] args) {
        try {
            var engine=new Engine();
            if(args.Length>0&&args[0]=="--build-test") {
                string fixtures=args[1],output=args[2];engine.Select(fixtures);Directory.CreateDirectory(output);
                for(int mask=0;mask<512;mask++)foreach(var f in engine.P.files) {
                    byte[] source=File.ReadAllBytes(Path.Combine(fixtures,f.path));var b=engine.Build(f,source,mask);
                    if(mask==511){string p=Path.Combine(output,f.path);Directory.CreateDirectory(Path.GetDirectoryName(p));File.WriteAllBytes(p,b);}
                }
                return 0;
            }
            if(args.Length>0&&args[0]=="--apply-test") {engine.Apply(args[1],int.Parse(args[2]));return 0;}
            if(args.Length>0&&args[0]=="--restore-test") {engine.Restore(args[1]);return 0;}
            if(args.Length>0&&args[0]=="--render-ui") {Application.EnableVisualStyles();using(var f=new MainWindow()){f.StartPosition=FormStartPosition.Manual;f.Location=new Point(-30000,-30000);f.Show();Application.DoEvents();using(var b=new Bitmap(f.Width,f.Height)){f.DrawToBitmap(b,new Rectangle(0,0,b.Width,b.Height));b.Save(args[1]);}f.Close();}return 0;}
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Application.Run(new MainWindow());return 0;
        }catch(Exception ex){if(args.Length>0){File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"test-error.txt"),ex.ToString());return 1;}MessageBox.Show(ex.Message);return 1;}
    }
}
