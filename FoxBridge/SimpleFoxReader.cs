using System;
using System.Data;
using System.IO;
using System.Text;
using System.Collections.Generic;

namespace FoxBridge
{
    // Clase pública para exponer la estructura
    public class FoxColumn
    {
        public string Name;
        public char Type;
        public int Length;
        public int Decimal;
    }

    public class SimpleFoxReader
    {
        public int HeaderRecordCount { get; private set; } = 0;
        public List<FoxColumn> Columns { get; private set; } = new List<FoxColumn>();

        public DataTable ReadDBF(string filepath)
        {
            if (!File.Exists(filepath)) throw new FileNotFoundException("DBF not found", filepath);

            this.Columns.Clear();

            using (var fs = new FileStream(filepath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var br = new BinaryReader(fs))
            {
                
                byte version = br.ReadByte();
                br.ReadBytes(3); 
                this.HeaderRecordCount = br.ReadInt32();
                short headerBytes = br.ReadInt16();
                short recordBytes = br.ReadInt16();
 
                fs.Seek(32, SeekOrigin.Begin);

                var table = new DataTable();

          
                while (fs.Position < headerBytes - 1)
                {
                    byte first = br.ReadByte();
 
                    if (first == 0x0D || first == 0x00)
                    {
                        break;
                    }

                    fs.Seek(-1, SeekOrigin.Current);  

                    byte[] fieldNameBytes = br.ReadBytes(11);
                    string colName = Encoding.ASCII.GetString(fieldNameBytes).TrimEnd((char)0);

                    char colType = (char)br.ReadByte();
                    br.ReadInt32();  
                    byte colLen = br.ReadByte();
                    byte colDec = br.ReadByte();
                    fs.Seek(14, SeekOrigin.Current); 

                    if (string.IsNullOrWhiteSpace(colName)) continue;

                    var col = new FoxColumn { Name = colName, Type = colType, Length = colLen, Decimal = colDec };
                    this.Columns.Add(col);
                    table.Columns.Add(colName, typeof(string));
                }

               
                fs.Seek(headerBytes, SeekOrigin.Begin);

                for (int i = 0; i < HeaderRecordCount; i++)
                {
                    if (fs.Position >= fs.Length) break;

                    byte deleteFlag = br.ReadByte();
                    if (deleteFlag == 0x2A) 
                    {
                        fs.Seek(recordBytes - 1, SeekOrigin.Current);
                        continue;
                    }

                    DataRow row = table.NewRow();
                    foreach (var col in Columns)
                    {
                         
                        if (fs.Position + col.Length > fs.Length) break;

                        byte[] raw = br.ReadBytes(col.Length);
                        string val = Encoding.GetEncoding(1252).GetString(raw);
                        row[col.Name] = val;
                    }
                    table.Rows.Add(row);
                }

                return table;
            }
        }
    }
}