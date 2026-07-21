using System;
using System.Data;
using System.IO;
using System.Text;

namespace FoxBridge
{
    public static class FoxCsvWriter
    {
        public static string WriteCsv(DataTable table)
        {
            string csvPath = Path.GetTempFileName();
            var encoding = new UTF8Encoding(false);

            using (var writer = new StreamWriter(csvPath, false, encoding))
            {
                writer.NewLine = "\n";

                foreach (DataRow row in table.Rows)
                {
                    var clean = new string[table.Columns.Count];
                    for (int i = 0; i < table.Columns.Count; i++)
                    {
                        string val = row[i]?.ToString()?.Trim() ?? "";

                   
                        if (val.Contains("\\")) val = val.Replace("\\", "\\\\");

                    
                        val = val.Replace("\t", " ")
                                 .Replace("\n", " ")
                                 .Replace("\r", " ")
                                 .Trim();

                        clean[i] = val;
                    }
                    writer.Write(string.Join("\t", clean));
                    writer.Write("\n");
                }
            }
            return csvPath;
        }
    }
}