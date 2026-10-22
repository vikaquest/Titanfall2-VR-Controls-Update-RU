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

[assembly: AssemblyTitle("Titanfall 2 VR — Апдейт управления")]
[assembly: AssemblyVersion("1.3.0.0")]
[assembly: AssemblyFileVersion("1.3.0.0")]
[assembly: AssemblyInformationalVersion("1.3.0")]

public class Op { public int offset; public int remove; public string data; public int any; }
public class Variant { public Op[] ops; public string hash; }
public class FileRecipe { public string path,hash,sourcePath,adoptHash; public bool optional; public int length,mask; public Op[] ops; public Dictionary<string,Variant> variants; }
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
        if(f.optional && (mask&f.mask)==0)return null;
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
            if(!File.Exists(path) && !f.optional) throw new Exception("Не найден файл: "+f.path+"\nСначала установите VR-мод через CircuitLordVRModInstaller.");
            string h=File.Exists(path)?Hash(File.ReadAllBytes(path)):null;
            if(!f.optional && s!=null && !s.current.ContainsKey(f.path)) {
                if(h!=f.hash && !(P.legacy??new Legacy[0]).Any(x=>x.path==f.path&&x.hash==h))throw new Exception("Неизвестный файл новой опции: "+f.path);
                s.current[f.path]=h;
            }
            if(s!=null&&s.initial!=null&&!s.initial.ContainsKey(f.path))s.initial[f.path]=f.optional?null:h;
            if(f.optional && s!=null && !s.current.ContainsKey(f.path)) {
                if(h!=null&&h!=f.adoptHash)throw new Exception("Неизвестный дополнительный скрипт: "+f.path);
                s.current[f.path]=h;if(s.initial!=null)s.initial[f.path]=null;
            }
            if(s!=null) { if(!s.current.ContainsKey(f.path)||h!=s.current[f.path]) throw new Exception("Файл изменён после установки патча: "+f.path+"\nВозможно, VR-мод обновился. Запись остановлена; резервные копии сохранены."); }
            stock &= f.optional?h==null:h==f.hash;var prior=(P.legacy??new Legacy[0]).FirstOrDefault(x=>x.path==f.path);legacy &= f.optional?h==null:prior!=null&&h==prior.hash;
        }
        if(s!=null) {
            foreach(var f in P.files) {string bp=FilePath(root,BackupRel("baseline/"+f.path));if(!File.Exists(bp)){if(f.optional ? Hash(File.ReadAllBytes(FilePath(root,f.sourcePath)))!=f.hash : (Hash(File.ReadAllBytes(FilePath(root,f.path)))!=f.hash && !(P.legacy??new Legacy[0]).Any(x=>x.path==f.path&&x.hash==Hash(File.ReadAllBytes(FilePath(root,f.path))))))throw new Exception("Неизвестная исходная версия скрипта комплектов титана.");}else if(Hash(File.ReadAllBytes(bp))!=f.hash) throw new Exception("Повреждена резервная копия.");}
            return s;
        }
        if(!stock&&!legacy) throw new Exception("Эта сборка VR-мода не поддерживается. Требуются проверенные исходные файлы Titanfall 2 VR 1.0.13, 1.0.14 или 1.0.17.\nПатч не изменяет неизвестные DLL и скрипты.");
        foreach(var f in P.files.Where(x=>x.optional))if(Hash(File.ReadAllBytes(FilePath(root,f.sourcePath)))!=f.hash)throw new Exception("Неизвестная исходная версия скрипта комплектов титана.");
        return new State { mask=legacy?511:0,imported=legacy,current=P.files.ToDictionary(x=>x.path,x=>File.Exists(FilePath(root,x.path))?Hash(File.ReadAllBytes(FilePath(root,x.path))):null) };
    }
    void ClosedGame() {
        foreach(var p in Process.GetProcesses()) { try { string n=p.ProcessName.ToLowerInvariant(); if(n=="titanfall2"||n=="northstar"||n=="titanfall2vr")throw new InvalidOperationException("Закройте игру перед изменением файлов."); } finally { p.Dispose(); } }
    }
    public void Apply(string root,int mask) {
        ClosedGame();root=Path.GetFullPath(root);
        // Reload on Trigger must free the same Trigger's pilot ability binding.
        if((mask&1)!=0 && (mask&2)==0) throw new Exception("Для перезарядки на левом Trigger включите перенос способности пилота на L3.");
        var s=Check(root);
        int supported=P.features.Aggregate(0,(bits,f)=>bits|f.bit);
        if(mask<0||(mask&~supported)!=0)throw new Exception("Эти функции не поддерживаются выбранной версией VR-мода.");
        if((mask&1024)!=0&&(mask&128)==0)throw new Exception("Для случайного оружия включите обмен оружия в кобурах на B.");
        string dir=FilePath(root,BackupRel());Directory.CreateDirectory(dir);
        using(var gate=new FileStream(FilePath(root,BackupRel("lock")),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None)) {
            s=Check(root);
            var bases=new Dictionary<string,byte[]>();
            foreach(var f in P.files) {
                string bp=FilePath(root,BackupRel("baseline/"+f.path));
                byte[] b;
                if(LoadState(root)==null) {
                    bool present=File.Exists(FilePath(root,f.path));
                    b=File.ReadAllBytes(FilePath(root,f.optional?f.sourcePath:f.path));
                    var initialPath=FilePath(root,BackupRel("initial/"+f.path));Directory.CreateDirectory(Path.GetDirectoryName(initialPath));
                    if(File.Exists(initialPath) && Hash(File.ReadAllBytes(initialPath))!=s.current[f.path])throw new Exception("Обнаружена предыдущая резервная копия. Не удаляйте её; установка остановлена.");
                    if(present&&!File.Exists(initialPath))File.WriteAllBytes(initialPath,File.ReadAllBytes(FilePath(root,f.path)));
                    if(s.imported&&!f.optional)b=Edit(b,P.legacy.First(x=>x.path==f.path).ops);
                    if(Hash(b)!=f.hash)throw new Exception("Не удалось восстановить исходную версию.");
                    Directory.CreateDirectory(Path.GetDirectoryName(bp));
                    if(File.Exists(bp)&&Hash(File.ReadAllBytes(bp))!=f.hash)throw new Exception("Резервная копия повреждена.");
                    if(!File.Exists(bp))File.WriteAllBytes(bp,b);
                } else {if(f.optional&&!File.Exists(bp)){b=File.ReadAllBytes(FilePath(root,f.sourcePath));if(Hash(b)!=f.hash)throw new Exception("Неизвестная исходная версия скрипта.");Directory.CreateDirectory(Path.GetDirectoryName(bp));File.WriteAllBytes(bp,b);}else if(!File.Exists(bp)){
                    b=File.ReadAllBytes(FilePath(root,f.path));string h=Hash(b);
                    string ip=FilePath(root,BackupRel("initial/"+f.path));Directory.CreateDirectory(Path.GetDirectoryName(ip));
                    if(!File.Exists(ip))File.WriteAllBytes(ip,b);
                    if(h!=f.hash){var prior=(P.legacy??new Legacy[0]).FirstOrDefault(x=>x.path==f.path&&x.hash==h);if(prior==null)throw new Exception("Неизвестный файл новой опции.");b=Edit(b,prior.ops);}
                    if(Hash(b)!=f.hash)throw new Exception("Не удалось восстановить исходный файл новой опции.");
                    Directory.CreateDirectory(Path.GetDirectoryName(bp));File.WriteAllBytes(bp,b);
                }else b=File.ReadAllBytes(bp);}
                bases[f.path]=b;
            }
            if(s.initial==null)s.initial=new Dictionary<string,string>(s.current);
            var output=P.files.ToDictionary(f=>f.path,f=>Build(f,bases[f.path],mask));
            s.mask=mask;s.current=output.ToDictionary(x=>x.Key,x=>x.Value==null?null:Hash(x.Value));
            output[BackupRel("state.json")]=Encoding.UTF8.GetBytes(Json.Serialize(s));
            Transaction(root,output);
        }
    }
    static void Atomic(string path,byte[] bytes) {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        if(bytes==null){if(File.Exists(path))File.Delete(path);return;}
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
                if(pair.Value==null?File.Exists(path):Hash(File.ReadAllBytes(path))!=Hash(pair.Value))throw new Exception("Не удалось проверить записанный файл.");
            }
            Directory.Delete(pending,true);
        } catch { Recover(root);throw; }
    }
    void Recover(string root) {
        string pending=FilePath(root,BackupRel("pending"));string journal=Path.Combine(pending,"journal.json");
        if(!File.Exists(journal))throw new Exception("Отсутствует журнал восстановления. Файлы не перезаписаны.");
        var before=Json.Deserialize<Dictionary<string,string>>(File.ReadAllText(journal));
        var allowed=new HashSet<string>(P.files.Select(f=>f.path));allowed.Add(BackupRel("state.json"));
        var required=new HashSet<string>(P.files.Where(f=>!f.optional).Select(f=>f.path));required.Add(BackupRel("state.json"));
        if(before.Keys.Any(k=>!allowed.Contains(k))||required.Any(k=>!before.ContainsKey(k)))throw new Exception("Некорректный журнал восстановления.");
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
            foreach(var f in P.files) {byte[] b=s.initial[f.path]==null?null:File.ReadAllBytes(FilePath(root,BackupRel("initial/"+f.path)));if((b==null?null:Hash(b))!=s.initial[f.path])throw new Exception("Повреждена исходная копия.");output[f.path]=b;}
            s.current=new Dictionary<string,string>(s.initial);s.mask=s.imported?511:0;
            output[BackupRel("state.json")]=Encoding.UTF8.GetBytes(Json.Serialize(s));Transaction(root,output);
        }
    }
}

public class FeatureCopy { public string id,group,groupEn,title,titleEn,description,descriptionEn,note,noteEn; public string[] controls,controlsEn; }
public static class Words {
#if EN
    public static readonly bool English=true;
#else
    public static readonly bool English=false;
#endif
    public static string T(string ru,string en){return English?en:ru;}
    public static string Error(string message){
        if(!English)return message;
        foreach(var pair in Errors.OrderByDescending(x=>x.Key.Length))message=message.Replace(pair.Key,pair.Value);
        return message;
    }
    public static Dictionary<string,string> Errors=new Dictionary<string,string>();
}
public static class Theme {
    public static Color Background=Color.FromArgb(17,23,29),Card=Color.FromArgb(25,34,43),Text=Color.FromArgb(229,237,243),Muted=Color.FromArgb(168,184,199),Border=Color.FromArgb(52,69,85),Accent=Color.FromArgb(104,188,232),Selected=Color.FromArgb(32,57,73);
    public static System.Drawing.Drawing2D.GraphicsPath Round(RectangleF r,float radius){var p=new System.Drawing.Drawing2D.GraphicsPath();float d=radius*2;p.AddArc(r.X,r.Y,d,d,180,90);p.AddArc(r.Right-d,r.Y,d,d,270,90);p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);p.AddArc(r.X,r.Bottom-d,d,d,90,90);p.CloseFigure();return p;}
}
public class DarkCheckBox:CheckBox {
    public DarkCheckBox(){SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true);Cursor=Cursors.Hand;}
    protected override void OnPaint(PaintEventArgs e){e.Graphics.Clear(Parent.BackColor);e.Graphics.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;var r=new RectangleF(1,1,16,16);using(var p=Theme.Round(r,2))using(var brush=new SolidBrush(Checked?Theme.Accent:Theme.Card))using(var pen=new Pen(Theme.Border)){e.Graphics.FillPath(brush,p);if(!Checked)e.Graphics.DrawPath(pen,p);}if(Checked)using(var pen=new Pen(Theme.Background,2.3f)){e.Graphics.DrawLines(pen,new PointF[]{new PointF(4,9),new PointF(7,12),new PointF(14,5)});}if(Focused)ControlPaint.DrawFocusRectangle(e.Graphics,new Rectangle(0,0,19,19),Theme.Accent,Parent.BackColor);}
}
public class DarkCard:Panel {
    public Color Stroke=Theme.Border;
    public DarkCard(){DoubleBuffered=true;BackColor=Theme.Card;}
    protected override void OnPaintBackground(PaintEventArgs e){e.Graphics.Clear(Parent==null?Theme.Background:Parent.BackColor);}
    protected override void OnPaint(PaintEventArgs e){e.Graphics.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;using(var p=Theme.Round(new RectangleF(0.5f,0.5f,Width-1,Height-1),7))using(var brush=new SolidBrush(BackColor))using(var pen=new Pen(Stroke)){e.Graphics.FillPath(brush,p);e.Graphics.DrawPath(pen,p);}base.OnPaint(e);}
}
public class FeatureRow:DarkCard {
    public CheckBox Box=new DarkCheckBox(); public Label Caption=new Label(); public Feature Feature; public FeatureCopy Copy;
    public bool Selected; public event EventHandler Pick;
    public FeatureRow(Feature f,FeatureCopy copy){
        Feature=f;Copy=copy;Height=40;Margin=new Padding(0,0,0,3);BackColor=Theme.Card;
        Box.SetBounds(13,10,20,20);Box.Checked=true;Box.AccessibleName=Words.T(copy.title,copy.titleEn);Controls.Add(Box);
        Caption.Text=Words.T(copy.title,copy.titleEn);Caption.SetBounds(42,1,302,38);Caption.TextAlign=ContentAlignment.MiddleLeft;Caption.Font=new Font("Segoe UI",9.5f);Caption.ForeColor=Theme.Text;Caption.BackColor=Color.Transparent;Caption.Cursor=Cursors.Hand;Controls.Add(Caption);
        Caption.Click+=(s,e)=>{if(Pick!=null)Pick(this,EventArgs.Empty);};Click+=(s,e)=>{if(Pick!=null)Pick(this,EventArgs.Empty);};Box.Enter+=(s,e)=>{if(Pick!=null)Pick(this,EventArgs.Empty);};
    }
    protected override void OnPaint(PaintEventArgs e){Stroke=Selected?Theme.Accent:Theme.Border;base.OnPaint(e);}
}
public class MainWindow:Form {
    Engine engine=new Engine(); TextBox folder=new TextBox(); Panel list=new Panel(); DarkCard detail=new DarkCard(); Label status=new Label(),count=new Label(),heading=new Label(),description=new Label(),conditions=new Label(),section=new Label(); FlowLayoutPanel bindings=new FlowLayoutPanel();
    Button apply=new Button(),restore=new Button(); List<FeatureRow> rows=new List<FeatureRow>(); List<FeatureCopy> copies; bool updating; string selectedId;
    Color ink=Theme.Text,muted=Theme.Muted,accent=Theme.Accent,line=Theme.Border;
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]static extern int DwmSetWindowAttribute(IntPtr window,int attr,ref int value,int size);
    protected override void OnHandleCreated(EventArgs e){base.OnHandleCreated(e);try{int enabled=1;DwmSetWindowAttribute(Handle,20,ref enabled,4);}catch{}}
    public MainWindow(){
        using(var s=Assembly.GetExecutingAssembly().GetManifestResourceStream("features.json"))using(var r=new StreamReader(s,Encoding.UTF8))copies=Engine.Json.Deserialize<List<FeatureCopy>>(r.ReadToEnd());
        using(var s=Assembly.GetExecutingAssembly().GetManifestResourceStream("messages.json"))using(var r=new StreamReader(s,Encoding.UTF8))Words.Errors=Engine.Json.Deserialize<Dictionary<string,string>>(r.ReadToEnd());
        Text=Words.T("Titanfall 2 VR — Апдейт управления v1.3.0","Titanfall 2 VR — Controls Update v1.3.0");ClientSize=new Size(1120,860);MinimumSize=MaximumSize=Size;FormBorderStyle=FormBorderStyle.FixedSingle;MaximizeBox=false;StartPosition=FormStartPosition.CenterScreen;Font=new Font("Segoe UI",10);BackColor=Theme.Background;ForeColor=ink;AutoScaleMode=AutoScaleMode.None;
        var title=new Label{Text="Titanfall 2 VR",Font=new Font("Segoe UI",24,FontStyle.Bold),Location=new Point(28,17),Size=new Size(600,45),ForeColor=ink};Controls.Add(title);
        Controls.Add(new Label{Text=Words.T("Апдейт управления 1.3.0  /  Quest & Touch","Controls Update 1.3.0  /  Quest & Touch"),Location=new Point(31,66),Size=new Size(1000,24),ForeColor=muted});
        folder.BackColor=Theme.Card;folder.ForeColor=Theme.Text;folder.BorderStyle=BorderStyle.FixedSingle;folder.SetBounds(31,103,920,28);folder.Text=Directory.Exists(@"D:\SteamLibrary\steamapps\common\Titanfall2")?@"D:\SteamLibrary\steamapps\common\Titanfall2":"";Controls.Add(folder);
        var browse=MakeButton(Words.T("Папка игры…","Game folder…"),962,100,127,false);browse.Click+=(s,e)=>{using(var d=new FolderBrowserDialog{Description=Words.T("Выберите папку Titanfall2","Select your Titanfall2 folder"),SelectedPath=folder.Text})if(d.ShowDialog()==DialogResult.OK){folder.Text=d.SelectedPath;Inspect();}};
        Controls.Add(new Label{Text=Words.T("ВОЗМОЖНОСТИ","FEATURES"),Location=new Point(31,150),Size=new Size(353,22),ForeColor=muted,Font=new Font("Segoe UI",9,FontStyle.Bold)});
        list.SetBounds(31,180,366,565);list.AutoScroll=true;Controls.Add(list);
        detail.SetBounds(415,150,674,595);detail.BackColor=Theme.Card;Controls.Add(detail);
        section.SetBounds(27,23,614,24);section.ForeColor=accent;section.Font=new Font("Segoe UI",10,FontStyle.Bold);detail.Controls.Add(section);
        heading.SetBounds(27,59,615,96);heading.Font=new Font("Segoe UI",20,FontStyle.Bold);detail.Controls.Add(heading);
        description.SetBounds(29,163,610,86);description.Font=new Font("Segoe UI",12);description.ForeColor=muted;detail.Controls.Add(description);
        bindings.SetBounds(28,257,614,200);bindings.FlowDirection=FlowDirection.TopDown;bindings.WrapContents=false;bindings.AutoScroll=true;detail.Controls.Add(bindings);
        conditions.SetBounds(29,472,612,100);conditions.Font=new Font("Segoe UI",10);conditions.ForeColor=muted;detail.Controls.Add(conditions);
        count.SetBounds(31,761,370,23);count.Font=new Font("Segoe UI",10,FontStyle.Bold);Controls.Add(count);
        status.SetBounds(31,789,636,50);status.ForeColor=muted;status.Font=new Font("Segoe UI",9);Controls.Add(status);
        var check=MakeButton(Words.T("Проверить","Check files"),678,789,115,false);check.Click+=(s,e)=>Inspect();
        restore=MakeButton(Words.T("Восстановить","Restore"),802,789,125,false);restore.Click+=(s,e)=>Run(()=>{engine.Restore(folder.Text);Inspect();status.Text=Words.T("Восстановлено состояние из первоначального журнала.","Restored the state recorded in the original backup journal.");});
        apply=MakeButton(Words.T("Применить","Apply changes"),936,789,153,true);apply.Click+=(s,e)=>Run(()=>{engine.Apply(folder.Text,SelectionMask());status.Text=Words.T("Готово. Выбранные изменения установлены; файлы проверены.","Done. Selected changes were installed and verified.");});
        BuildRows();Shown+=(s,e)=>Inspect();
    }
    Button MakeButton(string text,int x,int y,int width,bool primary){var b=new Button{Text=text,Location=new Point(x,y),Size=new Size(width,38),FlatStyle=FlatStyle.Flat,BackColor=primary?accent:Theme.Card,ForeColor=primary?Theme.Background:ink};b.FlatAppearance.BorderColor=primary?accent:line;b.FlatAppearance.MouseOverBackColor=primary?Color.FromArgb(132,206,239):Theme.Selected;b.FlatAppearance.MouseDownBackColor=Theme.Selected;Controls.Add(b);return b;}
    void BuildRows(){updating=true;list.Controls.Clear();rows.Clear();int y=0;foreach(var copy in copies){var f=engine.P.features.FirstOrDefault(x=>x.id==copy.id);if(f==null)continue;var row=new FeatureRow(f,copy);row.SetBounds(0,y,346,40);row.Pick+=(s,e)=>SelectFeature(row.Feature.id);row.Box.CheckedChanged+=(s,e)=>{if(updating)return;updating=true;try{if(f.bit==1&&row.Box.Checked)SetBit(2,true);if(f.bit==2&&!row.Box.Checked)SetBit(1,false);if(f.bit==1024&&row.Box.Checked)SetBit(128,true);if(f.bit==128&&!row.Box.Checked)SetBit(1024,false);}finally{updating=false;}UpdateCount();};list.Controls.Add(row);rows.Add(row);y+=43;}updating=false;UpdateCount();SelectFeature(rows.Any(x=>x.Feature.id==selectedId)?selectedId:rows[0].Feature.id);}
    void SetBit(int bit,bool value){var row=rows.FirstOrDefault(x=>x.Feature.bit==bit);if(row!=null)row.Box.Checked=value;}
    void UpdateCount(){count.Text=Words.T("Выбрано ","Selected ")+rows.Count(x=>x.Box.Checked)+Words.T(" из "," of ")+rows.Count+Words.T(" возможностей"," features");}
    int SelectionMask(){return rows.Where(x=>x.Box.Checked).Aggregate(0,(n,x)=>n|x.Feature.bit);}
    public void SelectFeature(string id){var row=rows.FirstOrDefault(x=>x.Feature.id==id);if(row==null)return;selectedId=id;foreach(var r in rows){r.Selected=r==row;r.BackColor=r.Selected?Theme.Selected:Theme.Card;r.Invalidate();}var c=row.Copy;section.Text=Words.T(c.group,c.groupEn).ToUpperInvariant()+Words.T("  /  УПРАВЛЕНИЕ","  /  CONTROLS");heading.Text=Words.T(c.title,c.titleEn);description.Text=Words.T(c.description,c.descriptionEn);conditions.Text=Words.T(c.note,c.noteEn);bindings.Controls.Clear();foreach(var key in Words.English?c.controlsEn:c.controls){bindings.Controls.Add(new Label{Text=key,AutoSize=false,Size=new Size(590,28),Margin=new Padding(0,0,0,4),Padding=new Padding(9,4,4,0),BackColor=Theme.Selected,ForeColor=accent,Font=new Font("Segoe UI",10,FontStyle.Bold)});}if(row.Top<list.VerticalScroll.Value||row.Bottom>list.VerticalScroll.Value+list.ClientSize.Height)list.ScrollControlIntoView(row);}
    public void CaptureGallery(string directory){Directory.CreateDirectory(directory);foreach(var row in rows){SelectFeature(row.Feature.id);Application.DoEvents();using(var b=new Bitmap(Width,Height)){DrawToBitmap(b,new Rectangle(0,0,Width,Height));b.Save(Path.Combine(directory,row.Feature.id+".png"));}}}
    public void VerifyUI(){if(rows.Count!=13||SelectionMask()!=8191)throw new Exception("UI defaults failed");SetBit(2,false);if((SelectionMask()&3)!=0)throw new Exception("Reload dependency failed");SetBit(1,true);if((SelectionMask()&3)!=3)throw new Exception("Cloak dependency failed");SetBit(128,false);if((SelectionMask()&1024)!=0)throw new Exception("Random dependency failed");foreach(var row in rows)row.Box.Checked=true;foreach(var row in rows)SelectFeature(row.Feature.id);if(SelectionMask()!=8191)throw new Exception("Checkbox selection failed");SelectFeature(rows[0].Feature.id);}
    void Inspect(){try{engine.Check(folder.Text);if(rows.Count!=engine.P.features.Length)BuildRows();status.Text=Words.T("Найден VR-мод ","Detected VR mod ")+engine.P.version+Words.T(". Перед применением закройте игру. Резервные копии сохраняются в папке игры.",". Close the game before applying. Backups are stored in the game folder.");apply.Enabled=true;}catch(Exception ex){status.Text=Words.Error(ex.Message.Split('\n')[0]);apply.Enabled=false;}}
    void Run(Action action){try{apply.Enabled=restore.Enabled=false;UseWaitCursor=true;action();}catch(Exception ex){status.Text=Words.T("Изменения не применены.","Changes were not applied.");MessageBox.Show(this,Words.Error(ex.Message),Text,MessageBoxButtons.OK,MessageBoxIcon.Warning);}finally{UseWaitCursor=false;restore.Enabled=true;apply.Enabled=true;}}
}

public static class Program {
    [STAThread] public static int Main(string[] args) {
        try {
            using(var s=Assembly.GetExecutingAssembly().GetManifestResourceStream("messages.json"))using(var r=new StreamReader(s,Encoding.UTF8))Words.Errors=Engine.Json.Deserialize<Dictionary<string,string>>(r.ReadToEnd());
            var engine=new Engine();
            if(args.Length>0&&(args[0]=="--render-gallery"||args[0]=="--ui-test")) {
                Application.EnableVisualStyles();using(var f=new MainWindow()){f.StartPosition=FormStartPosition.Manual;f.Location=new Point(-30000,-30000);f.Show();Application.DoEvents();f.VerifyUI();if(args[0]=="--render-gallery")f.CaptureGallery(args[1]);f.Close();}return 0;
            }
            if(args.Length>0&&args[0]=="--build-test") {
                string fixtures=args[1],output=args[2];engine.Select(fixtures);Directory.CreateDirectory(output);
                for(int mask=0;mask<(1<<engine.P.features.Length);mask++)foreach(var f in engine.P.files) {
                    byte[] source=File.ReadAllBytes(Path.Combine(fixtures,f.optional?f.sourcePath:f.path));var b=engine.Build(f,source,mask);
                    if(mask==((1<<engine.P.features.Length)-1)&&b!=null){string p=Path.Combine(output,f.path);Directory.CreateDirectory(Path.GetDirectoryName(p));File.WriteAllBytes(p,b);}
                }
                return 0;
            }
            if(args.Length>0&&args[0]=="--apply-test") {engine.Apply(args[1],int.Parse(args[2]));return 0;}
            if(args.Length>0&&args[0]=="--restore-test") {engine.Restore(args[1]);return 0;}
            if(args.Length>0&&args[0]=="--render-ui") {Application.EnableVisualStyles();using(var f=new MainWindow()){f.StartPosition=FormStartPosition.Manual;f.Location=new Point(-30000,-30000);f.Show();Application.DoEvents();using(var b=new Bitmap(f.Width,f.Height)){f.DrawToBitmap(b,new Rectangle(0,0,b.Width,b.Height));b.Save(args[1]);}f.Close();}return 0;}
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Application.Run(new MainWindow());return 0;
        }catch(Exception ex){if(args.Length>0){File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"test-error.txt"),Words.Error(ex.ToString()));return 1;}MessageBox.Show(Words.Error(ex.Message));return 1;}
    }
}
