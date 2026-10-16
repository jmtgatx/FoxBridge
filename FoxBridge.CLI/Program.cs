using System;
using System.IO;
using System.Data;
using System.Configuration;
using MySql.Data.MySqlClient;
using FoxBridge;
using System.Net.NetworkInformation;

namespace FoxBridge.CLI
{
    class Program
    {
     
        static int Main(string[] args)
        { 
            try
            {
                Console.WriteLine("=== FoxBridge CLI ===");

                if (args.Length < 1)
                {
                    Console.WriteLine("Usage: FoxBridge.exe <filename_or_path> [target_table]");
                    Console.WriteLine("       FoxBridge.exe --xml <xml-output>");
                    Console.WriteLine("       FoxBridge.exe --fox <filename_or_path>");
                    return 1;
                }

               
                string connStr = ConfigurationManager.ConnectionStrings["ArtAppConnection"]?.ConnectionString;
                string basePath = ConfigurationManager.AppSettings["DbfSourcePath"] ?? "";
                string useCustomPathStr = ConfigurationManager.AppSettings["UseCustomPath"] ?? "true";
                bool useCustomPath = bool.TryParse(useCustomPathStr, out bool res) ? res : true;
                string stagingSuffix = ConfigurationManager.AppSettings["StagingSuffix"] ?? "_stg";

                if (args[0].Equals("--fox", StringComparison.OrdinalIgnoreCase))
                {
                    if (args.Length != 2)
                    {
                        Console.WriteLine("Usage: FoxBridge.exe --fox <filename_or_path>");
                        return 1;
                    }

                    string outputDirectory = ConfigurationManager.AppSettings["XML_output"] ?? basePath;
                    if (string.IsNullOrWhiteSpace(outputDirectory))
                    {
                        outputDirectory = AppDomain.CurrentDomain.BaseDirectory;
                    }

                    string outputName = Path.GetFileNameWithoutExtension(args[1]);
                    if (string.IsNullOrWhiteSpace(outputName))
                    {
                        throw new ArgumentException("The FoxPro input must contain a file name.", nameof(args));
                    }

                    string sqlPath = Path.Combine(basePath, "FoxProXmlQuery.txt");
                    if (!File.Exists(sqlPath))
                    {
                        sqlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FoxProXmlQuery.txt");
                    }

                    string foxDbfPath = Path.Combine(outputDirectory, $"UPS_{outputName}.dbf");
                    var importer = new FoxImporter();
                    importer.FoxCommand(sqlPath, basePath, foxDbfPath);
                   // int rowsExported = importer.ExportQueryToFox(basePath, sqlPath, foxDbfPath);
                    //Console.WriteLine($"✔ EXPORTED: {rowsExported} records from FoxPro SQL to '{foxDbfPath}'.");
                    return 0;
                }

                if (args[0].Equals("--xml", StringComparison.OrdinalIgnoreCase))
                {
                    if (args.Length != 2)
                    {
                        Console.WriteLine("Usage: FoxBridge.exe --xml <xml-output>");
                        return 1;
                    }

                    string xmlOutputDirectory = ConfigurationManager.AppSettings["XML_output"] ?? basePath;
                    if (string.IsNullOrWhiteSpace(xmlOutputDirectory))
                    {
                        xmlOutputDirectory = AppDomain.CurrentDomain.BaseDirectory;
                    }

                    string outputFileName = Path.GetFileName(args[1]);
                    if (string.IsNullOrWhiteSpace(outputFileName) || !args[1].Equals(outputFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new ArgumentException("The XML output argument must be a file name, not a path.", nameof(args));
                    }

                    string sqlPath = Path.Combine(basePath, "FoxProXmlQuery.txt");
                    if (!File.Exists(sqlPath))
                    {
                        sqlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FoxProXmlQuery.txt");
                    }

                    string xmlPath = Path.Combine(xmlOutputDirectory, outputFileName);
                    var importer = new FoxImporter();
                    int rowsExported = importer.ExportQueryToXml(basePath, sqlPath, xmlPath);
                    Console.WriteLine($"✔ EXPORTED: {rowsExported} records from FoxPro SQL to '{xmlPath}' with schema.");
                    return 0;
                }

                string minRowsStr = ConfigurationManager.AppSettings["MinRowsToSwap"] ?? "0";
                int minRowsToSwap = int.TryParse(minRowsStr, out int mr) ? mr : 0;
 
                string inputArg = args[0];
                string dbfPath = useCustomPath ? inputArg : Path.Combine(basePath, inputArg);

                if (!File.Exists(dbfPath)) throw new FileNotFoundException($"DBF file not found: {dbfPath}"); 
        
                string outputTableName;

                if (args.Length >= 2)
                { 
                    outputTableName = args[1];
                }
                else
                { 
                    outputTableName = Path.GetFileNameWithoutExtension(dbfPath);
                }
                 
                string finalTable = outputTableName;
                 
                string stagingTable = finalTable;

                if (!string.IsNullOrEmpty(stagingSuffix))
                {
                
                    if (finalTable.EndsWith(stagingSuffix, StringComparison.OrdinalIgnoreCase))
                    {
                        finalTable = finalTable.Substring(0, finalTable.Length - stagingSuffix.Length);
                    }

                    

                    if(args.Length >= 2)
                        stagingTable = finalTable;
                    else
                        stagingTable = finalTable + stagingSuffix;


                } 
     
                 
                var watch = System.Diagnostics.Stopwatch.StartNew();
                int rowsImported = 0;

                using (var conn = new MySqlConnection(connStr))
                {
                    conn.Open();
                    var importer = new FoxImporter();
                    Console.WriteLine($"Processing: {Path.GetFileName(dbfPath)}");
                    Console.WriteLine($"Strategy: Staging='{stagingTable}' -> Final='{finalTable}'");
                     
                    rowsImported = importer.Import(dbfPath, stagingTable, conn);
                    int foxHeaderCount = importer.LastHeaderCount;

                    Console.WriteLine($"✔ READ: FoxPro Header claims {foxHeaderCount} records.");
                    Console.WriteLine($"✔ IMPORTED: {rowsImported} records loaded into '{stagingTable}'.");

                  
                    if (stagingTable != finalTable)
                    { 
                        if (foxHeaderCount == 0)
                        {
                            string msg = "SAFETY ABORT: FoxPro header count is 0.";
                            AbortWithLog(conn, finalTable, msg, watch.ElapsedMilliseconds);
                            return 1;
                        }
                         
                        if (rowsImported < minRowsToSwap)
                        {
                            string msg = $"SAFETY ABORT: Imported ({rowsImported}) < Config Min ({minRowsToSwap}).";
                            AbortWithLog(conn, finalTable, msg, watch.ElapsedMilliseconds);
                            return 1;
                        }
                         
                        double dropRate = 1.0 - ((double)rowsImported / (double)foxHeaderCount);
                        if (dropRate > 0.20)
                        {
                            string msg = $"SAFETY ABORT: High data loss! FoxPro: {foxHeaderCount}, MySQL: {rowsImported}. (Loss: {dropRate:P1})";
                            AbortWithLog(conn, finalTable, msg, watch.ElapsedMilliseconds);
                            return 1;
                        }
                         
                        Console.WriteLine("   [SWAP] Validations passed. Swapping...");
                        importer.SyncToProduction(stagingTable, finalTable, conn);
                    }

                    watch.Stop();
                    LogToDb(conn, finalTable, rowsImported, "OK", "Sync complete", watch.ElapsedMilliseconds);
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\n❌ CRITICAL ERROR: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                Console.ResetColor();

                try
                {
                    string cStr = ConfigurationManager.ConnectionStrings["ArtAppConnection"]?.ConnectionString;
                    using (var conn = new MySqlConnection(cStr))
                    {
                        conn.Open();
                        string tName = (args != null && args.Length > 0) ? Path.GetFileNameWithoutExtension(args[0]) : "UNKNOWN";
                        LogToDb(conn, tName, 0, "CRASH", ex.Message, 0);
                    }
                }
                catch { }

                return 1;
            }
        }

        private static void AbortWithLog(MySqlConnection conn, string table, string msg, long ms)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"⛔ {msg}");
            Console.ResetColor();
            LogToDb(conn, table, 0, "SKIPPED_SAFETY", msg, ms);
        }

        private static void LogToDb(MySqlConnection conn, string tableName, int rows, string status, string message, long ms)
        {
            try
            {
                string createSql = @"CREATE TABLE IF NOT EXISTS `fox_sync_log` (
                      `id` INT NOT NULL AUTO_INCREMENT,
                      `table_name` VARCHAR(100),
                      `sync_date` DATETIME DEFAULT CURRENT_TIMESTAMP,
                      `rows_affected` INT,
                      `status` VARCHAR(20),
                      `message` TEXT,
                      `duration_ms` INT,
                      PRIMARY KEY (`id`)
                    ) ENGINE=InnoDB;";
                using (var cmd = new MySqlCommand(createSql, conn)) cmd.ExecuteNonQuery();

                string sql = @"INSERT INTO fox_sync_log (table_name, rows_affected, status, message, duration_ms) 
                               VALUES (@table, @rows, @status, @msg, @ms)";
                using (var cmd = new MySqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@table", tableName);
                    cmd.Parameters.AddWithValue("@rows", rows);
                    cmd.Parameters.AddWithValue("@status", status);
                    cmd.Parameters.AddWithValue("@msg", message);
                    cmd.Parameters.AddWithValue("@ms", ms);
                    cmd.ExecuteNonQuery();
                }
                Console.WriteLine("   [LOG] Entry saved to DB.");
            }
            catch (Exception ex) { Console.WriteLine($"⚠️ Log Failed: {ex.Message}"); }
        }
    }
}