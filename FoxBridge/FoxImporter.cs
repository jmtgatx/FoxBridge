using System;
using System.Collections.Generic;
using System.Data;
using System.Data.OleDb;
using MySql.Data.MySqlClient;
using System.IO;
using System.Text;
using System.Linq;
using System.Threading;

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

        public int ExportToXml(string dbfPath, string xmlPath)
        {
            DataTable dbfData = _reader.ReadDBF(dbfPath);
            string tableName = Path.GetFileNameWithoutExtension(dbfPath);

            if (string.IsNullOrWhiteSpace(tableName))
            {
                tableName = "Table1";
            }

            dbfData.TableName = tableName;

            var dataSet = new DataSet("FoxProDataSet");
            dataSet.Tables.Add(dbfData);
            dataSet.WriteXml(xmlPath, XmlWriteMode.WriteSchema);

            return dbfData.Rows.Count;
        }

        public int ExportQueryToXml(string sourceDirectory, string sqlPath, string xmlPath)
        {
            var dataSet = new DataSet("Addresses");
            var dataTable = LoadQueryDataTable(sourceDirectory, sqlPath);
            dataTable.TableName = "Address";
            dataSet.Tables.Add(dataTable);

            string outputDirectory = Path.GetDirectoryName(Path.GetFullPath(xmlPath));
            if (!string.IsNullOrEmpty(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            dataSet.WriteXml(xmlPath, XmlWriteMode.WriteSchema);
            return dataTable.Rows.Count;
        }

        public int ExportQueryToFox(string sourceDirectory, string sqlPath, string dbfPath)
        {
            if (!Directory.Exists(sourceDirectory))
            {
                throw new DirectoryNotFoundException($"FoxPro source directory not found: {sourceDirectory}");
            }

            string ordersPath = Path.Combine(sourceDirectory, "soshad01.dbf");
            string addressesPath = Path.Combine(sourceDirectory, "aradrs01.dbf");
            DataTable orders = _reader.ReadDBF(ordersPath);
            DataTable addresses = _reader.ReadDBF(addressesPath);
            DataTable dataTable = JoinOrdersToAddresses(orders, addresses);
            string outputDirectory = Path.GetDirectoryName(Path.GetFullPath(dbfPath));
            if (!string.IsNullOrEmpty(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            if (File.Exists(dbfPath))
            {
                File.Delete(dbfPath);
            }

            _reader.WriteDBF(dataTable, dbfPath);

            return dataTable.Rows.Count;
        }

        private DataTable JoinOrdersToAddresses(DataTable orders, DataTable addresses)
        {
            string orderCustomerColumn = FindColumn(orders, "custno");
            string orderCompanyColumn = FindColumn(orders, "company");
            string addressCustomerColumn = FindColumn(addresses, "custno");
            string addressCompanyColumn = FindColumn(addresses, "company");
            string addressEmailColumn = FindColumn(addresses, "email");

            var addressByKey = new Dictionary<string, DataRow>(StringComparer.OrdinalIgnoreCase);
            foreach (DataRow address in addresses.Rows)
            {
                string key = JoinKey(address[addressCustomerColumn], address[addressCompanyColumn]);
                if (!addressByKey.ContainsKey(key))
                {
                    addressByKey.Add(key, address);
                }
            }

            var result = new DataTable();
            foreach (DataColumn column in orders.Columns)
            {
                result.Columns.Add(column.ColumnName, typeof(string));
            }
            result.Columns.Add("email", typeof(string));

            foreach (DataRow order in orders.Rows)
            {
                string key = JoinKey(order[orderCustomerColumn], order[orderCompanyColumn]);
                DataRow address;
                if (!addressByKey.TryGetValue(key, out address))
                {
                    continue;
                }

                DataRow resultRow = result.NewRow();
                foreach (DataColumn column in orders.Columns)
                {
                    resultRow[column.ColumnName] = order[column] == DBNull.Value
                        ? ""
                        : order[column].ToString().Trim();
                }
                resultRow["email"] = address[addressEmailColumn] == DBNull.Value
                    ? ""
                    : address[addressEmailColumn].ToString().Trim();
                result.Rows.Add(resultRow);
            }

            string orderNumberColumn = FindColumn(result, "sono");
            var sortedRows = result.AsEnumerable()
                .OrderByDescending(row => SortValue(row[orderNumberColumn]))
                .ToList();
            var sortedResult = result.Clone();
            foreach (DataRow row in sortedRows)
            {
                sortedResult.ImportRow(row);
            }

            return sortedResult;
        }

        private string FindColumn(DataTable table, string columnName)
        {
            foreach (DataColumn column in table.Columns)
            {
                if (column.ColumnName.Equals(columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return column.ColumnName;
                }
            }

            throw new InvalidDataException($"Required FoxPro column '{columnName}' was not found.");
        }

        private string JoinKey(object customer, object company)
        {
            string customerValue = customer == DBNull.Value ? "" : customer.ToString().Trim();
            string companyValue = company == DBNull.Value ? "" : company.ToString().Trim();
            return customerValue + "\u001f" + companyValue;
        }

        private decimal SortValue(object value)
        {
            decimal result;
            return decimal.TryParse(value == DBNull.Value ? "" : value.ToString().Trim(), out result)
                ? result
                : decimal.MinValue;
        }

        private DataTable LoadQueryDataTable(string sourceDirectory, string sqlPath)
        {
            if (!Directory.Exists(sourceDirectory))
            {
                throw new DirectoryNotFoundException($"FoxPro source directory not found: {sourceDirectory}");
            }

            if (!File.Exists(sqlPath))
            {
                throw new FileNotFoundException("FoxPro SQL file not found", sqlPath);
            }

            var dataTable = new DataTable();
            string sql = File.ReadAllText(sqlPath);
            string connectionString = $"Provider=VFPOLEDB.1;Data Source={sourceDirectory};Pooling=False;Mode=Share Deny None;";
            using (var connection = new OleDbConnection(connectionString))
            using (var adapter = new OleDbDataAdapter(sql, connection))
            {
                adapter.Fill(dataTable);
            }

            foreach (DataRow row in dataTable.Rows)
            {
                foreach (DataColumn column in dataTable.Columns)
                {
                    if (row[column] == DBNull.Value) continue;
                    string value = row[column].ToString().Trim();
                    if (column.ColumnName.Equals("billoption", StringComparison.OrdinalIgnoreCase))
                    {
                        switch (value.ToUpperInvariant())
                        {
                            case "SHP": value = "Shipper"; break;
                            case "REC": value = "Receiver"; break;
                            case "TP": value = "Third Party"; break;
                        }
                    }
                    row[column] = value;
                }
            }

            return dataTable;
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
        public void FoxCommand(string commandPath, string sourceDirectory, string foxDbfPath)
        {
            if (string.IsNullOrWhiteSpace(commandPath)) throw new ArgumentNullException(nameof(commandPath));

            string dbfConnectionString = $"Provider=VFPOLEDB.1;Data Source={sourceDirectory};Pooling=False;Mode=Share Deny None;";
            int maxAttempts = 3;
            int delayMs = 5000;

            string sql = File.ReadAllText(commandPath);

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(foxDbfPath) && File.Exists(foxDbfPath))
                    {
                        File.Delete(foxDbfPath);
                    }

                    using (OleDbConnection connection = new OleDbConnection(dbfConnectionString))
                    {
                        connection.Open();
                        using (OleDbCommand command = new OleDbCommand(sql, connection))
                        {
                            command.ExecuteNonQuery();
                        }
                    }  

                    return;  
                }
                catch (Exception)
                {
                    if (attempt == maxAttempts)
                    {
                        throw; 
                    }

                    Thread.Sleep(delayMs);  
                }
            }
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