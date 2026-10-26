using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using VibeMeters.Models;

namespace VibeMeters.Services;

/// <summary>Localizes presentation only. Provider snapshots and protocol identifiers remain unchanged.</summary>
public static class Localizer
{
    private static readonly KeyValuePair<string,string>[] Catalog = Load();
    private static readonly ConditionalWeakTable<DependencyObject, Dictionary<DependencyProperty, Original>> Originals = new();
    private sealed class Original { public string Source = ""; public string Last = ""; }
    public static AppLanguage Language { get; private set; }
    public static bool IsEnglish => Language == AppLanguage.English || Language == AppLanguage.System &&
        !CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("ko",StringComparison.OrdinalIgnoreCase);
    public static CultureInfo Culture => CultureInfo.GetCultureInfo(IsEnglish ? "en-US" : "ko-KR");
    public static event Action? Changed;
    public static void SetLanguage(AppLanguage language)
    {
        if (Language == language) return;
        Language = language; Changed?.Invoke();
    }
    private static KeyValuePair<string,string>[] Load()
    {
        using var stream = typeof(Localizer).Assembly.GetManifestResourceStream("VibeMeters.Resources.Strings.en.txt")
            ?? throw new InvalidOperationException("UI translations are missing.");
        using var reader = new StreamReader(stream);
        var items = new Dictionary<string,string>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
        {
            var split = line.IndexOf('|');
            if (split <= 0 || line.StartsWith('#')) continue;
            items.Add(line[..split].Replace("\\n","\n").Replace("\\s"," "),line[(split+1)..].Replace("\\n","\n").Replace("\\s"," "));
        }
        return items.OrderByDescending(pair=>pair.Key.Length).ToArray();
    }
    public static string T(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        if (!IsEnglish || !Regex.IsMatch(text,"[가-힣]")) return text;
        text = Regex.Replace(text,@"조회 일시 제한(.*) · ([0-9/: ]+) 이후 재시도",m=>"Lookup rate limited"+m.Groups[1].Value+" · Retry after "+m.Groups[2].Value);
        text = Regex.Replace(text,@"남은 사용량 ([\d.,]+)% · 사용량 창에서 초기화 시간을 확인하세요\.","Quota remaining: $1%. See the quota window for reset times.");
        foreach (var pair in Catalog) text = text.Replace(pair.Key,pair.Value,StringComparison.Ordinal);
        text = Regex.Replace(text,@"(?<=\d)시간","h");
        text = Regex.Replace(text,@"(?<=\d)분","m");
        text = Regex.Replace(text,@"(?<=\d)초","s");
        text = Regex.Replace(text,@"(?<=\d)일","d");
        return text;
    }
    public static void Track(Window window)
    {
        var applying = false;
        void Refresh()
        {
            if (applying) return;
            applying = true;
            try { Apply(window); } finally { applying = false; }
        }
        RoutedEventHandler loaded = (_,_)=>Refresh();
        window.Loaded += loaded;
        Changed += Refresh;
        window.Closed += (_,_)=> { Changed -= Refresh; window.Loaded -= loaded; };
        Refresh();
    }
    public static void Apply(DependencyObject root)
    {
        var seen = new HashSet<DependencyObject>();
        void Property(DependencyObject node,DependencyProperty property)
        {
            if (node.GetValue(property) is not string current || current.Length == 0) return;
            var values = Originals.GetOrCreateValue(node);
            if (!values.TryGetValue(property,out var original)) values[property] = original = new Original { Source=current,Last=current };
            if (current != original.Last) original.Source=current;
            var translated=T(original.Source); original.Last=translated;
            if (translated != current) node.SetCurrentValue(property,translated);
        }
        void Visit(DependencyObject node)
        {
            if (!seen.Add(node)) return;
            if (node is Window) Property(node,Window.TitleProperty);
            if (node is TextBlock) Property(node,TextBlock.TextProperty);
            if (node is Run) Property(node,Run.TextProperty);
            if (node is ContentControl) Property(node,ContentControl.ContentProperty);
            if (node is HeaderedContentControl) Property(node,HeaderedContentControl.HeaderProperty);
            Property(node,AutomationProperties.NameProperty); Property(node,AutomationProperties.HelpTextProperty);
            if (node is FrameworkElement element)
            {
                Property(node,FrameworkElement.ToolTipProperty);
                if (element.ToolTip is DependencyObject tip) Visit(tip);
            }
            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()) Visit(child);
            if (node is ItemsControl items) foreach (var item in items.Items.OfType<DependencyObject>()) Visit(item);
            if (node is Visual || node is System.Windows.Media.Media3D.Visual3D)
                for (var i=0;i<VisualTreeHelper.GetChildrenCount(node);i++) Visit(VisualTreeHelper.GetChild(node,i));
        }
        Visit(root);
    }
}
