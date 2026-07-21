using System;
using System.Collections.Generic;
using System.Data;
using MySql.Data.MySqlClient;
using System.IO;
using System.Text;
using System.Linq;

namespace FoxBridge
{
    public class FoxImporter
    {
        private readonly SimpleFoxReader _reader = new SimpleFoxReader();

     
        public int LastHeaderCount { get; private set; }

        public int Import(string dbfPath, string tableName, MySqlConnection conn)
        {
      
            DataTable dbfData = _reader.ReadDBF(dbfPath);
            this.LastHeaderCount = _reader.HeaderRecordCount;
 
            EnsureStagingTableExists(tableName, conn, _reader.Columns);

       
            return BulkInsertManual(dbfData, tableName, conn);
        }

        private int BulkInsertManual(DataTable sourceDt, string tableName, MySqlConnection conn)
        {
            if (sourceDt.Rows.Count == 0) return 0;

        
            var colTypes = new Dictionary<string, Type>();
            var colNames = new List<string>();

            using (var cmd = new MySqlCommand($"SELECT * FROM {tableName} LIMIT 0", conn))
            using (var reader = cmd.ExecuteReader())
            {
                var schema = reader.GetSchemaTable();
                foreach (DataRow row in schema.Rows)
                {
                    string name = row["ColumnName"].ToString();
                    colNames.Add(name);
                    colTypes[name] = (Type)row["DataType"];
                }
            }

           
            StringBuilder sbBase = new StringBuilder();
            sbBase.Append($"INSERT INTO `{tableName}` (`{string.Join("`, `", colNames)}`) VALUES ");
            string sqlHeader = sbBase.ToString();

       
            int batchSize = 2000; 
            int totalRows = 0;
            var valueRows = new List<string>(batchSize);

            using (var trans = conn.BeginTransaction())
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = trans;
                cmd.CommandTimeout = 120;  

                foreach (DataRow sourceRow in sourceDt.Rows)
                {
                    var cleanValues = new List<string>();

                    foreach (string col in colNames)
                    {
                    
                        object rawVal = null;
                        int idx = -1;
                        for (int k = 0; k < sourceDt.Columns.Count; k++)
                        {
                            if (sourceDt.Columns[k].ColumnName.Equals(col, StringComparison.OrdinalIgnoreCase))
                            {
                                idx = k; break;
                            }
                        }

                        if (idx >= 0)
                        {
                            rawVal = sourceRow[idx];
                            if (rawVal is string s)
                            {
                                rawVal = s.Trim();
                            }
                        }

                       
                        string sqlVal = SanitizeForSql(rawVal, colTypes[col]);
                        cleanValues.Add(sqlVal);
                    }

                    
                    valueRows.Add($"({string.Join(",", cleanValues)})");
                    totalRows++;

                    
                    if (valueRows.Count >= batchSize)
                    {
                        ExecuteBatch(cmd, sqlHeader, valueRows);
                    }
                }

             
                if (valueRows.Count > 0)
                {
                    ExecuteBatch(cmd, sqlHeader, valueRows);
                }

                trans.Commit();  
            }

            return totalRows;
        }

        private void ExecuteBatch(MySqlCommand cmd, string header, List<string> rows)
        {
            var sb = new StringBuilder(header);
            sb.Append(string.Join(",", rows));
            sb.Append(";");

            cmd.CommandText = sb.ToString();
            cmd.ExecuteNonQuery();

            rows.Clear();
        }

        private string SanitizeForSql(object val, Type targetType)
        {
           
            if (val == null || val == DBNull.Value || string.IsNullOrWhiteSpace(val.ToString()))
            {
          
                if (targetType == typeof(DateTime)) return "'1899-12-30'";
                if (IsNumeric(targetType)) return "0";
                return "''";
            }

            string str = val.ToString();

           
            if (targetType == typeof(DateTime))
            {
                if (str.Contains("0000-00-00") || str.Trim() == "/  /") return "'1899-12-30'"; 
                return $"'{MySqlHelper.EscapeString(str)}'";
            }

           
            if (IsNumeric(targetType))
            {
        
                return str.Trim();
            }

         
            return $"'{MySqlHelper.EscapeString(str)}'";
        }

        private bool IsNumeric(Type type)
        {
            if (type == null) return false;
            TypeCode tc = Type.GetTypeCode(type);
            return (tc == TypeCode.Double || tc == TypeCode.Decimal ||
                    tc == TypeCode.Int32 || tc == TypeCode.Int64 ||
                    tc == TypeCode.Int16 || tc == TypeCode.Byte);
        }

      

        private void EnsureStagingTableExists(string stagingTable, MySqlConnection conn, List<FoxColumn> columns)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"SHOW TABLES LIKE '{stagingTable}';";
                if (cmd.ExecuteScalar() != null)
                {  
                    cmd.CommandText = $"TRUNCATE TABLE {stagingTable}";
                    cmd.ExecuteNonQuery();
                    return;
                }
            }
 
            string sql = GenerateSqlCreate(stagingTable, columns);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
            Console.WriteLine($"✔ Auto-created '{stagingTable}' from DBF structure.");
        }

        private string GenerateSqlCreate(string tableName, List<FoxColumn> columns)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"CREATE TABLE `{tableName}` (");
            List<string> defs = new List<string>();
            foreach (var col in columns)
            {
                string mysqlType = "VARCHAR(255)";
                switch (col.Type)
                {
                    case 'C': mysqlType = $"VARCHAR({col.Length})"; break;
                    case 'D': case 'T': mysqlType = "DATETIME"; break;
                    case 'N':
                    case 'F':
                        if (col.Decimal == 0) mysqlType = (col.Length > 9) ? "BIGINT" : "INT";
                        else mysqlType = $"DECIMAL({col.Length}, {col.Decimal})";
                        break;
                    case 'L': mysqlType = "TINYINT(1)"; break;
                    case 'I': mysqlType = "INT"; break;
                    case 'M': mysqlType = "TEXT"; break;
                }
                defs.Add($"  `{col.Name}` {mysqlType} DEFAULT NULL");
            }
            sb.AppendLine(string.Join(",\n", defs));
            sb.AppendLine(") ENGINE=InnoDB DEFAULT CHARSET=utf8;");
            return sb.ToString();
        }

        public void SyncToProduction(string stagingTable, string prodTable, MySqlConnection conn)
        {
            string backupTable = prodTable + "_old";
            Console.WriteLine($"   [SYNC] Iniciando Swap: {stagingTable} -> {prodTable}");

            using (var cmd = conn.CreateCommand())
            {
              
                cmd.CommandText = $"CREATE TABLE IF NOT EXISTS {prodTable} LIKE {stagingTable};";
                cmd.ExecuteNonQuery();

                try
                {
               
                    cmd.CommandText = "SET SESSION lock_wait_timeout = 10;"; cmd.ExecuteNonQuery();
                    cmd.CommandText = $"DROP TABLE IF EXISTS {backupTable};"; cmd.ExecuteNonQuery();
                 
                    cmd.CommandText = $"RENAME TABLE {prodTable} TO {backupTable}, {stagingTable} TO {prodTable};";
                    cmd.ExecuteNonQuery();

                    cmd.CommandText = $"DROP TABLE {backupTable};"; cmd.ExecuteNonQuery();
                    Console.WriteLine($"✔ [EXITO] Tabla '{prodTable}' actualizada.");
                }
                catch (MySqlException ex) { Console.WriteLine($"❌ ERROR EN SWAP: {ex.Message}"); }
            }
        }
    }
}