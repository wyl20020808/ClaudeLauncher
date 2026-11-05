using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
namespace ClaudeLauncher {
    internal static class Appearance {
        internal static int Theme=1, Language;
        internal static bool Dark { get { if(Theme!=0) return Theme==2; try { using(var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")) return key!=null && Object.Equals(key.GetValue("AppsUseLightTheme"),0); } catch { return false; } } }
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        internal static int ColorRef(Color color) { return color.R | (color.G << 8) | (color.B << 16); }
        internal static bool ApplyWindowFrame(Window window) {
            IntPtr handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return false;
            int dark = Dark ? 1 : 0;
            int caption = ColorRef(Brush("#FAF6F0").Color), text = ColorRef(Brush("#2B2622").Color);
            int border = -2; // DWMWA_COLOR_NONE removes the system accent border.
            DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
            // Windows 11 caption attributes return an unsupported HRESULT on older Windows.
            int captionResult = DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
            int textResult = DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
            int borderResult = DwmSetWindowAttribute(handle, 34, ref border, sizeof(int));
            return captionResult >= 0 && textResult >= 0 && borderResult >= 0;
        }
        static readonly Dictionary<string,SolidColorBrush> brushes=new Dictionary<string,SolidColorBrush>();
        sealed class ColorValue : System.ComponentModel.INotifyPropertyChanged {
            Color value;
            public Color Value { get { return value; } set { this.value=value; if(PropertyChanged!=null) PropertyChanged(this,new System.ComponentModel.PropertyChangedEventArgs("Value")); } }
            public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        }
        static readonly Dictionary<string,ColorValue> colors=new Dictionary<string,ColorValue>();
        static readonly Dictionary<string,string> translations=Load();
        static readonly Dictionary<string,string> translatedCache=new Dictionary<string,string>();
        static Dictionary<string,string> Load() {
            var result=new Dictionary<string,string>();
            using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("ClaudeLauncher.English")) {
                if(stream==null) return result;
                using(var reader=new StreamReader(stream,Encoding.UTF8)) {
                    foreach(Match match in Regex.Matches(reader.ReadToEnd(),"\"((?:\\\\.|[^\"\\\\])*)\"\\s*=\\s*\"((?:\\\\.|[^\"\\\\])*)\"\\s*;")) {
                        string source=Regex.Unescape(match.Groups[1].Value), target=Regex.Unescape(match.Groups[2].Value);
                        target=target.Replace("on this Mac","on this PC").Replace("On this Mac","On this PC").Replace("on your Mac","on your PC");
                        if(Regex.Matches(source,"%(?:@|[0-9.]*[dfiul])").Cast<Match>().Select(m=>m.Value).SequenceEqual(Regex.Matches(target,"%(?:@|[0-9.]*[dfiul])").Cast<Match>().Select(m=>m.Value))) result[source]=target;
                    }
                }
            }
            return result;
        }
        internal static string Text(string source) {
            if(Language!=1||source==null) return source;
            string translated; if(translations.TryGetValue(source,out translated)) return translated;
            if(translatedCache.TryGetValue(source,out translated)) return translated;
            if(translatedCache.Count>2000) translatedCache.Clear();
            // 字面内容最长的优先，否则 "%@ %@" 这样的宽泛词条会抢走具体词条。
            foreach(var pair in translations.Where(p=>p.Key.Contains("%@")||Regex.IsMatch(p.Key,"%[0-9.]*[ldiu]")).OrderByDescending(p=>Regex.Replace(p.Key,"%[0-9.]*[ldiu@]+","").Length)) {
                string pattern="^"+Regex.Replace(Regex.Escape(pair.Key),"%[0-9.]*[ldiu@]+","(.*?)")+"$";
                Match match=Regex.Match(source,pattern); if(!match.Success) continue;
                int index=0; return translatedCache[source]=Regex.Replace(pair.Value,"%[0-9.]*[ldiu@]+",m=>match.Groups[++index].Value);
            }
            return translatedCache[source]=source;
        }
        static Color ConvertColor(string value) {
            Color color=(Color)ColorConverter.ConvertFromString(value);
            if(!Dark || color.A==0 || value=="#FFFEFD") return color;
            double max=Math.Max(color.R,Math.Max(color.G,color.B)), min=Math.Min(color.R,Math.Min(color.G,color.B));
            if(max-min>65 && max<220) return color;
            double light=(max+min)/510.0;
            Color mapped=(Color)ColorConverter.ConvertFromString(light>.88?"#28231F":light>.7?"#443B33":light<.32?"#F0E6DA":light<.65?"#C5B6A5":"#918171"); mapped.A=color.A; return mapped;
        }
        internal static SolidColorBrush Brush(string value) {
            SolidColorBrush brush;
            if(!brushes.TryGetValue(value,out brush)) {
                var color=new ColorValue { Value=ConvertColor(value) }; colors[value]=color;
                brushes[value]=brush=new SolidColorBrush();
                System.Windows.Data.BindingOperations.SetBinding(brush,SolidColorBrush.ColorProperty,new System.Windows.Data.Binding("Value") { Source=color });
            }
            return brush;
        }
        internal static void Refresh() { foreach(var pair in colors) pair.Value.Value=ConvertColor(pair.Key); }
        internal static GradientStop Stop(string color,double offset) {
            var stop=new GradientStop { Offset=offset };
            System.Windows.Data.BindingOperations.SetBinding(stop,GradientStop.ColorProperty,new System.Windows.Data.Binding("Color") { Source=Brush(color) }); return stop;
        }
        internal static string PrepareStyles(string xaml,ResourceDictionary resources) {
            int index=0;
            xaml=xaml.Replace("Property='Foreground' Value='White'","Property='Foreground' Value='#FFFEFD'");
            xaml=xaml.Replace("Fill='White'","Fill='#FFFEFD'");
            return Regex.Replace(xaml,"(Value|Background|Foreground|BorderBrush|Fill|Stroke)='(#[A-Fa-f0-9]{6,8}|White)'",m=> { string key="ThemeBrush"+(index++); resources[key]=Brush(m.Groups[2].Value); return m.Groups[1].Value+"='{DynamicResource "+key+"}'"; });
        }
        sealed class Original { internal string Raw,Shown; }
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DependencyObject,Dictionary<DependencyProperty,Original>> originals=new System.Runtime.CompilerServices.ConditionalWeakTable<DependencyObject,Dictionary<DependencyProperty,Original>>();
        static void Translate(DependencyObject node,DependencyProperty property) {
            string value=node.GetValue(property) as string; if(value==null) return;
            var items=originals.GetOrCreateValue(node); Original old;
            if(!items.TryGetValue(property,out old)||value!=old.Shown) items[property]=old=new Original { Raw=value };
            old.Shown=Text(old.Raw); if(value!=old.Shown) node.SetCurrentValue(property,old.Shown);
        }
        internal static void TranslateTree(DependencyObject root) {
            var combo=root as ComboBox;
            if(combo!=null && combo.ItemTemplate==null && Language==1) {
                var template=new DataTemplate(); var label=new FrameworkElementFactory(typeof(TextBlock));
                label.SetBinding(TextBlock.TextProperty,new System.Windows.Data.Binding { Converter=new TranslationConverter() }); template.VisualTree=label; combo.ItemTemplate=template;
            }
            if(root is TextBlock) Translate(root,TextBlock.TextProperty);
            if(root is ContentControl) Translate(root,ContentControl.ContentProperty);
            if(root is HeaderedItemsControl) Translate(root,HeaderedItemsControl.HeaderProperty);
            if(root is FrameworkElement) Translate(root,FrameworkElement.ToolTipProperty);
            if(root is Window) Translate(root,Window.TitleProperty);
            foreach(object child in LogicalTreeHelper.GetChildren(root)) { var node=child as DependencyObject; if(node!=null) TranslateTree(node); }
        }
        sealed class TranslationConverter : System.Windows.Data.IValueConverter {
            public object Convert(object value,Type target,object parameter,System.Globalization.CultureInfo culture) { return Text(value as string)??value; }
            public object ConvertBack(object value,Type target,object parameter,System.Globalization.CultureInfo culture) { throw new NotSupportedException(); }
        }
    }
}
