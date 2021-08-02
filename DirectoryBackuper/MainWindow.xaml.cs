using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Xml.Serialization;

namespace DirectoryBackuper {
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window {
        ViewModel vm;
        public MainWindow() {
            InitializeComponent();

            vm = new ViewModel();

            this.DataContext = vm;
        }

        private void Button_Click(object sender, RoutedEventArgs e) {
            Task.Run(() => {
                vm.Executing = true;
                var opes = new List<FileOperation>();//すべてのファイル操作の入れ物

                for (int i = 0; i < vm.DirPairs.Count; i++) {
                    var src = vm.DirPairs[i].Source;
                    var dest = vm.DirPairs[i].Destination;

                    //フォルダの存在チェック（DestinationはフォルダであればOK）
                    if (!Directory.Exists(src)) {
                        vm.Status = src+ "は存在しません。";
                        continue;
                    }
                    if (File.Exists(dest)) {
                        vm.Status = src + "はファイルです。フォルダを指定してください。";
                        continue;
                    }
                    if (!Directory.Exists(dest)) {
                        try {
                            Directory.CreateDirectory(dest);
                        } catch {
                            vm.Status = dest + "フォルダを作成できませんでした。パスや権限を確認してください。";
                            continue;
                        }
                    }

                    vm.Status = "Sourceのファイルリストを取得しています。";
                    var srcfs = GetFiles(src, src);
                    vm.Status = "Destinationのファイルリストを取得しています。";
                    var destfs = GetFiles(dest, dest);

                    //全く同じものは除外し、残った奴を詳しく比較する
                    //・srcにある奴でdestに同じInitBytesを持つものがあれば移動
                    //・srcにある奴で移動でないものはコピー
                    //・それ以外のものでdestに残っているものは削除（実際にはdeletedフォルダに移動する）
                    var sfs = srcfs.Except(destfs, new FileInformationComparer()).ToList();
                    var dfs = destfs.Except(srcfs, new FileInformationComparer()).ToList();

                    var srcchecked= new List<FileInformation>();

                    //移動をチェック
                    vm.Status = "移動されたファイルをチェックしています。";
                    foreach (var sf in sfs) {
                        var same = dfs.Where(df => FileInformationComparer.BytesEquals(sf.InitBytes, df.InitBytes)).FirstOrDefault();
                        if (same != null) {
                            opes.Add(new FileOperation(same.Path, sf.GetParentChangedPath(same.ParentDirectory), FileOperation.Operations.Move));
                            dfs.Remove(same);
                            srcchecked.Add(sf);
                        }
                    }

                    sfs = sfs.Except(srcchecked, new FileInformationComparer()).ToList();//チェックした奴を省く

                    //変更をチェック
                    vm.Status = "変更されたファイルをチェックしています。";
                    foreach (var sf in sfs) {
                        var changed = dfs.Where(df => sf.RelativePath==df.RelativePath && !FileInformationComparer.BytesEquals(sf.InitBytes, df.InitBytes)).FirstOrDefault();
                        if (changed != null) {
                            opes.Add(new FileOperation(sf.Path, changed.Path, FileOperation.Operations.Change));
                            dfs.Remove(changed);
                            srcchecked.Add(sf);
                        }
                    }


                    sfs = sfs.Except(srcchecked, new FileInformationComparer()).ToList();//チェックした奴を省く

                    //コピーをチェック
                    vm.Status = "新規作成されたファイルをチェックしています。";
                    foreach (var sf in sfs) {
                        opes.Add(new FileOperation(sf.Path, sf.GetParentChangedPath(dest), FileOperation.Operations.Copy));
                        srcchecked.Add(sf);
                    }

                    //削除をチェック
                    vm.Status = "削除されたファイルをチェックしています。";
                    foreach (var df in dfs) {
                        opes.Add(new FileOperation(df.Path, df.GetParentChangedPath(System.IO.Path.Combine(dest, "deleted")), FileOperation.Operations.Delete));
                    }
                }

                //プログレスバーの初期化
                vm.Max = opes.Count;
                vm.Val = 0;

                //FileOperationの通りに処理
                vm.Status = "処理ファイル数："+ opes.Count;
                foreach (var op in opes) {
                    vm.Val++;
                    var pdir = System.IO.Path.GetDirectoryName(op.Destination);
                    if (!Directory.Exists(pdir)) Directory.CreateDirectory(pdir);
                    switch (op.Operation) {
                        case FileOperation.Operations.Move:
                            vm.Status = "【Move】\t" + op.Source + "\t" + op.Destination;
                            try {
                                File.Move(op.Source, op.Destination);
                            } catch {
                                vm.Status = "【Failure】\tファイルの移動に失敗しました。";
                            }
                            break;
                        case FileOperation.Operations.Change:
                            vm.Status = "【Change】\t" + op.Source + "\t" + op.Destination;
                            try {
                                File.Copy(op.Source, op.Destination, true);
                            } catch {
                                vm.Status = "【Failure】\tファイルの上書きに失敗しました。";
                            }
                            break;
                        case FileOperation.Operations.Copy:
                            vm.Status = "【Copy】\t" + op.Source + "\t" + op.Destination;
                            try {
                                File.Copy(op.Source, op.Destination);
                            } catch {
                                vm.Status = "【Failure】\tファイルのコピーに失敗しました。";
                            }
                            break;
                        case FileOperation.Operations.Delete:
                            vm.Status = "【Delete】\t" + op.Source + "\t" + op.Destination;
                            try {
                                File.Move(op.Source, op.Destination);
                            } catch {
                                vm.Status = "【Failure】\tファイルの削除（deletedフォルダへ移動）に失敗しました。";
                            }
                            break;
                        default:
                            break;
                    }
                }

                try {
                    vm.Status = "Backup completed.";

                    //DirPairsの保存
                    XmlSerializer xs = new XmlSerializer(typeof(ObservableCollection<DirPair>));
                    using (var sw = new StreamWriter(vm.SettingsXmlPath, false, Encoding.UTF8)) {
                        xs.Serialize(sw, vm.DirPairs);
                    }

                    //ログ出力
                    File.WriteAllText(vm.LogPath, vm.Log, Encoding.UTF8);
                } catch {
                    vm.Status = "バックアップは正常に完了しましたが、設定とログファイルの保存に失敗しました。";
                }

                vm.Executing = false;
            });
        }

        private List<FileInformation> GetFiles(string parent, string dir) {
            var l = new List<FileInformation>();
            var fs = Directory.GetFiles(dir);
            foreach (var f in fs) l.Add(new FileInformation(parent, f));
            var ds = Directory.GetDirectories(dir);
            foreach (var d in ds) l.AddRange(GetFiles(parent, d));
            return l;
        }
    }
    public class ViewModel : INotifyPropertyChanged {
        public event PropertyChangedEventHandler PropertyChanged;
        public void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        Dispatcher dispatcher = Application.Current.MainWindow.Dispatcher;

        public string SettingsXmlPath {
            get { return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.xml"); }
        }
        public string LogPath {
            get {
                var logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, DateTime.Now.ToString("yyyyMMdd") + ".log");
                var index = 0;
                while (File.Exists(logPath)) {
                    logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, DateTime.Now.ToString("yyyyMMdd") + "_" + (++index) + ".log");
                }
                return logPath;
            }
        }

        public ObservableCollection<DirPair> DirPairs { get; set; } = new ObservableCollection<DirPair>();

        bool _Executing = false;
        public bool Executing {
            get { return _Executing; }
            set {
                _Executing = value;
                OnPropertyChanged(nameof(ExeButtonEnabled));
            }
        }
        public bool ExeButtonEnabled {
            get {
                return !Executing && DirPairs.Count > 0;
            }
        }

        public string Log { get; set; }//Statusをつなぎ合わせただけの物
        string _Status;
        public string Status {
            get { return _Status; }
            set {
                dispatcher.Invoke(() => {
                    Log += "[" + DateTime.Now.ToString("yyyy/MM/dd　HH:mm:ss") + "]\t" + value + "\r\n";
                    _Status = value;
                    OnPropertyChanged(nameof(Status));
                });
            }
        }
        public long Min { get; } = 0;
        long _Max = 100;
        public long Max {
            get { return _Max; }
            set {
                dispatcher.Invoke(() => {
                    _Max = value;
                    OnPropertyChanged(nameof(Max));
                });
            }
        }
        long _Val = 0;
        public long Val {
            get { return _Val; }
            set {
                dispatcher.Invoke(() => {
                    _Val = value;
                    OnPropertyChanged(nameof(Val));
                });
            }
        }
        public ViewModel() {
            DirPairs.CollectionChanged += (s, e) => {
                OnPropertyChanged(nameof(ExeButtonEnabled));
            };

            //settings.xmlがある場合は読み込む
            if (File.Exists(SettingsXmlPath)) {
                var xs = new XmlSerializer(typeof(ObservableCollection<DirPair>));
                using (var sr = new StreamReader(SettingsXmlPath, Encoding.UTF8)) {
                    DirPairs = (ObservableCollection<DirPair>)xs.Deserialize(sr);
                }
            }
        }
    }
    public class DirPair {
        public string Source { get; set; }
        public string Destination { get; set; }
    }
    /// <summary>
    /// ファイルをどう処理するかを格納するクラス
    /// </summary>
    public class FileOperation {
        public enum Operations {
            None,
            Copy,
            Move,
            Change,
            Delete
        }
        public string Source { get; set; }
        public string Destination { get; set; }
        public Operations Operation { get; set; }

        public FileOperation(string src, string dest, Operations ope) {
            Source = src;
            Destination = dest;
            Operation = ope;
        }
    }
    /// <summary>
    /// ファイルの情報を格納するクラス
    /// </summary>
    public class FileInformation {
        public string ParentDirectory { get; private set; }
        public string Path { get; set; }
        public string RelativePath { get; private set; }
        //ファイルが同じかどうかを判定するための最初の512→64バイトに変更
        byte[] _InitBytes = new byte[64];
        public byte[] InitBytes { get { return _InitBytes; } }
        public long Length { get; private set; }//長さも見ないとダメだよね。

        public FileInformation(string parentDir, string path) {
            ParentDirectory = parentDir;
            Path = path;
            RelativePath = System.IO.Path.GetRelativePath(ParentDirectory, Path);
            try {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read)) {
                    Length = fs.Length;
                    if (fs.Length < _InitBytes.Length) _InitBytes = new byte[fs.Length];
                    //先頭の64bytesを見るだけじゃ追加された場合なんかは確実にだめだし、ハッシュコード取得にも時間がかかるっぽいから
                    //独自の方法でファイルから64バイト取得する（最初512バイトでやってたけどものすごく遅かった）
                    //fs.Read(_InitBytes);
                    long step = Length / (long)_InitBytes.Length;
                    if (step < 1) step = 1;
                    for (long i = 0; i < _InitBytes.Length; i++) {
                        _InitBytes[i] = (byte)fs.ReadByte();
                        fs.Seek(step - 1, SeekOrigin.Current);
                    }
                }
            } catch {
                //無い場合は無いで良い（権限でエラーになった場合は知らん）
            }
        }

        /// <summary>
        /// 親フォルダを変更した場合のパスを返します
        /// </summary>
        /// <param name="parentDir">親フォルダ</param>
        /// <returns>親フォルダ変更後のパス</returns>
        public string GetParentChangedPath(string parentDir) {
            return System.IO.Path.GetFullPath(RelativePath, parentDir);
        }
    }
    public class FileInformationComparer : IEqualityComparer<FileInformation> {
        public bool Equals(FileInformation x, FileInformation y) {
            return x.RelativePath == y.RelativePath && x.Length == y.Length && BytesEquals(x.InitBytes, y.InitBytes);
        }

        public int GetHashCode([DisallowNull] FileInformation obj) {
            return obj.RelativePath.GetHashCode();
        }

        //dobon.netさんからコピペ（早いらしい）
        [System.Runtime.InteropServices.DllImport("msvcrt.dll", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
        private static extern int memcmp(byte[] b1, byte[] b2, UIntPtr count);
        public static bool BytesEquals(byte[] a, byte[] b) {
            if (object.ReferenceEquals(a, b)) {
                return true;
            }
            if (a == null || b == null || a.Length != b.Length) {
                return false;
            }

            return memcmp(a, b, new UIntPtr((uint)a.Length)) == 0;
        }
    }
    /// <summary>
    /// 0じゃないintをTrueで返すコンバーター
    /// </summary>
    public class NotZeroIntToTrueConverter : IValueConverter {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) {
            return ((int)value) > 0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) {
            var tmp = (bool)value;
            return tmp ? 1 : 0;
        }
    }
}
