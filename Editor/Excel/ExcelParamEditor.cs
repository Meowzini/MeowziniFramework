using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using UnityEditor;
using UnityEngine;

public class ExcelParamEditor : EditorWindow
{
    const float RowH = 20f;
    const float ColW = 120f;
    const float RowHeaderW = 40f;
    const float HeaderH = 20f;

    static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    static readonly XNamespace RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    static readonly XNamespace PkgRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";

    class Cell
    {
        public string text = "";
        public bool isFormula;
        public bool dirty;
    }

    class Sheet
    {
        public string name;
        public string entryPath;
        public int rows, cols;
        public Dictionary<(int r, int c), Cell> cells = new Dictionary<(int, int), Cell>();
    }

    string path;
    List<Sheet> sheets = new List<Sheet>();
    int sheetIndex;
    Vector2 scroll;
    string status = "";

    [MenuItem("Meowzini/Excel Param Editor")]
    static void Open() => GetWindow<ExcelParamEditor>("Excel Param Editor");


    void OnGUI()
    {
        DrawToolbar();

        if (sheets.Count == 0)
        {
            EditorGUILayout.HelpBox("Chọn một file .xlsx để bắt đầu.", MessageType.Info);
            return;
        }

        DrawGrid(sheets[sheetIndex]);

        if (!string.IsNullOrEmpty(status))
            EditorGUILayout.HelpBox(status, MessageType.None);
    }

    void DrawToolbar()
    {
        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            if (GUILayout.Button("Open", EditorStyles.toolbarButton, GUILayout.Width(50)))
            {
                string p = EditorUtility.OpenFilePanel("Chọn file Excel", Application.dataPath, "xlsx");
                if (!string.IsNullOrEmpty(p)) Load(p);
            }

            if (sheets.Count > 0)
            {
                if (GUILayout.Button("Reload", EditorStyles.toolbarButton, GUILayout.Width(55)))
                    Load(path);

                sheetIndex = EditorGUILayout.Popup(sheetIndex, sheets.Select(s => s.name).ToArray(),
                    EditorStyles.toolbarPopup, GUILayout.Width(140));

                GUILayout.Label(Path.GetFileName(path), EditorStyles.miniLabel);
            }

            GUILayout.FlexibleSpace();

            int dirty = sheets.Sum(s => s.cells.Values.Count(c => c.dirty));
            using (new EditorGUI.DisabledScope(dirty == 0))
            {
                if (GUILayout.Button($"Save ({dirty})", EditorStyles.toolbarButton, GUILayout.Width(80)))
                    Save();
            }
        }
    }

    void DrawGrid(Sheet sh)
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);

        float totalW = RowHeaderW + sh.cols * ColW;
        float totalH = HeaderH + sh.rows * RowH;
        Rect area = GUILayoutUtility.GetRect(totalW, totalH);

        int firstRow = Mathf.Max(0, (int)(scroll.y / RowH));
        int lastRow = Mathf.Min(sh.rows - 1, firstRow + (int)(position.height / RowH) + 2);
        int firstCol = Mathf.Max(0, (int)(scroll.x / ColW));
        int lastCol = Mathf.Min(sh.cols - 1, firstCol + (int)(position.width / ColW) + 2);

        for (int c = firstCol; c <= lastCol; c++)
        {
            var r = new Rect(area.x + RowHeaderW + c * ColW, area.y, ColW, HeaderH);
            GUI.Label(r, ColName(c), EditorStyles.centeredGreyMiniLabel);
        }

        for (int row = firstRow; row <= lastRow; row++)
        {
            float y = area.y + HeaderH + row * RowH;
            GUI.Label(new Rect(area.x, y, RowHeaderW, RowH - 2), (row + 1).ToString(),
                EditorStyles.centeredGreyMiniLabel);

            for (int c = firstCol; c <= lastCol; c++)
            {
                var rect = new Rect(area.x + RowHeaderW + c * ColW, y, ColW - 2, RowH - 2);
                sh.cells.TryGetValue((row, c), out Cell cell);
                string current = cell?.text ?? "";
                bool locked = cell != null && cell.isFormula;

                var oldBg = GUI.backgroundColor;
                if (cell != null && cell.dirty) GUI.backgroundColor = new Color(1f, 0.9f, 0.5f);

                EditorGUI.BeginChangeCheck();
                using (new EditorGUI.DisabledScope(locked))
                {
                    string next = EditorGUI.TextField(rect, current);
                    if (EditorGUI.EndChangeCheck())
                    {
                        if (cell == null)
                        {
                            cell = new Cell();
                            sh.cells[(row, c)] = cell;
                        }
                        cell.text = next;
                        cell.dirty = true;
                    }
                }
                GUI.backgroundColor = oldBg;
            }
        }

        EditorGUILayout.EndScrollView();
    }


    void Load(string filePath)
    {
        try
        {
            path = filePath;
            sheets.Clear();
            sheetIndex = 0;
            scroll = Vector2.zero;

            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                var shared = ReadSharedStrings(zip);

                var wb = ReadXml(zip, "xl/workbook.xml");
                var rels = ReadXml(zip, "xl/_rels/workbook.xml.rels");
                var relMap = rels.Root.Elements(PkgRelNs + "Relationship")
                    .ToDictionary(e => (string)e.Attribute("Id"), e => (string)e.Attribute("Target"));

                foreach (var s in wb.Root.Element(Ns + "sheets").Elements(Ns + "sheet"))
                {
                    string target = relMap[(string)s.Attribute(RelNs + "id")];
                    string entry = target.StartsWith("/") ? target.TrimStart('/') : "xl/" + target;

                    var sheet = new Sheet { name = (string)s.Attribute("name"), entryPath = entry };
                    ParseSheet(ReadXml(zip, entry), sheet, shared);
                    sheets.Add(sheet);
                }
            }

            status = $"Đã mở {Path.GetFileName(path)} ({sheets.Count} sheet).";
        }
        catch (Exception e)
        {
            sheets.Clear();
            status = "Không mở được file: " + e.Message;
            Debug.LogException(e);
        }
        Repaint();
    }

    static XDocument ReadXml(ZipArchive zip, string entryName)
    {
        var entry = zip.GetEntry(entryName) ?? throw new FileNotFoundException(entryName);
        using (var s = entry.Open()) return XDocument.Load(s);
    }

    static List<string> ReadSharedStrings(ZipArchive zip)
    {
        var list = new List<string>();
        if (zip.GetEntry("xl/sharedStrings.xml") == null) return list;
        var doc = ReadXml(zip, "xl/sharedStrings.xml");
        foreach (var si in doc.Root.Elements(Ns + "si"))
            list.Add(string.Concat(si.Descendants(Ns + "t").Select(t => t.Value)));
        return list;
    }

    static void ParseSheet(XDocument doc, Sheet sheet, List<string> shared)
    {
        var data = doc.Root.Element(Ns + "sheetData");
        if (data == null) return;

        foreach (var c in data.Descendants(Ns + "c"))
        {
            if (!ParseRef((string)c.Attribute("r"), out int row, out int col)) continue;

            string type = (string)c.Attribute("t");
            string text;
            if (type == "s")
            {
                int idx = int.Parse(c.Element(Ns + "v")?.Value ?? "0", CultureInfo.InvariantCulture);
                text = idx < shared.Count ? shared[idx] : "";
            }
            else if (type == "inlineStr")
            {
                text = string.Concat(c.Element(Ns + "is")?.Descendants(Ns + "t").Select(t => t.Value)
                                     ?? Enumerable.Empty<string>());
            }
            else if (type == "b")
            {
                text = c.Element(Ns + "v")?.Value == "1" ? "TRUE" : "FALSE";
            }
            else
            {
                text = c.Element(Ns + "v")?.Value ?? "";
            }

            bool isFormula = c.Element(Ns + "f") != null;
            if (type == "b") isFormula = true;

            sheet.cells[(row, col)] = new Cell { text = text, isFormula = isFormula };
            sheet.rows = Mathf.Max(sheet.rows, row + 1);
            sheet.cols = Mathf.Max(sheet.cols, col + 1);
        }
    }


    void Save()
    {
        try
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Update))
            {
                foreach (var sh in sheets)
                {
                    var dirtyCells = sh.cells.Where(kv => kv.Value.dirty && !kv.Value.isFormula).ToList();
                    if (dirtyCells.Count == 0) continue;

                    var doc = ReadXml(zip, sh.entryPath);
                    var sheetData = doc.Root.Element(Ns + "sheetData");

                    foreach (var kv in dirtyCells)
                        SetCell(sheetData, kv.Key.r, kv.Key.c, kv.Value.text);

                    zip.GetEntry(sh.entryPath).Delete();
                    var entry = zip.CreateEntry(sh.entryPath);
                    using (var s = entry.Open()) doc.Save(s);
                }
            }

            foreach (var sh in sheets)
                foreach (var cell in sh.cells.Values) cell.dirty = false;

            status = "Đã lưu lúc " + DateTime.Now.ToString("HH:mm:ss");
            AssetDatabase.Refresh();
        }
        catch (IOException)
        {
            status = "Không ghi được: file đang được mở bởi chương trình khác (Excel?). Hãy đóng file rồi lưu lại.";
        }
        catch (Exception e)
        {
            status = "Lỗi khi lưu: " + e.Message;
            Debug.LogException(e);
        }
        Repaint();
    }

    static void SetCell(XElement sheetData, int row, int col, string text)
    {
        XElement rowEl = null;
        XElement insertBeforeRow = null;
        foreach (var r in sheetData.Elements(Ns + "row"))
        {
            int rn = (int)r.Attribute("r") - 1;
            if (rn == row) { rowEl = r; break; }
            if (rn > row) { insertBeforeRow = r; break; }
        }
        if (rowEl == null)
        {
            rowEl = new XElement(Ns + "row", new XAttribute("r", row + 1));
            if (insertBeforeRow != null) insertBeforeRow.AddBeforeSelf(rowEl);
            else sheetData.Add(rowEl);
        }
        rowEl.Attribute("spans")?.Remove();

        // Tìm hoặc tạo <c>
        XElement cEl = null;
        XElement insertBeforeCell = null;
        foreach (var c in rowEl.Elements(Ns + "c"))
        {
            ParseRef((string)c.Attribute("r"), out _, out int cn);
            if (cn == col) { cEl = c; break; }
            if (cn > col) { insertBeforeCell = c; break; }
        }
        if (cEl == null)
        {
            cEl = new XElement(Ns + "c", new XAttribute("r", ColName(col) + (row + 1)));
            if (insertBeforeCell != null) insertBeforeCell.AddBeforeSelf(cEl);
            else rowEl.Add(cEl);
        }

        // Xóa nội dung cũ, giữ nguyên style (attribute "s")
        cEl.Attribute("t")?.Remove();
        cEl.Elements(Ns + "v").Remove();
        cEl.Elements(Ns + "is").Remove();

        if (string.IsNullOrEmpty(text)) return;

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double num))
        {
            cEl.Add(new XElement(Ns + "v", num.ToString("R", CultureInfo.InvariantCulture)));
        }
        else
        {
            cEl.Add(new XAttribute("t", "inlineStr"));
            var t = new XElement(Ns + "t", text);
            t.SetAttributeValue(XNamespace.Xml + "space", "preserve");
            cEl.Add(new XElement(Ns + "is", t));
        }
    }

    // ───────────────────────── Tiện ích ─────────────────────────

    static string ColName(int c)
    {
        string s = "";
        c++;
        while (c > 0)
        {
            c--;
            s = (char)('A' + c % 26) + s;
            c /= 26;
        }
        return s;
    }

    // "B3" → row=2, col=1 (đều tính từ 0)
    static bool ParseRef(string cellRef, out int row, out int col)
    {
        row = col = 0;
        if (string.IsNullOrEmpty(cellRef)) return false;
        int i = 0;
        int c = 0;
        while (i < cellRef.Length && char.IsLetter(cellRef[i]))
        {
            c = c * 26 + (char.ToUpperInvariant(cellRef[i]) - 'A' + 1);
            i++;
        }
        if (i == 0 || !int.TryParse(cellRef.Substring(i), out int r)) return false;
        col = c - 1;
        row = r - 1;
        return true;
    }
}