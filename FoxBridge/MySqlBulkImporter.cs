using System;
using System.Data;
using MySql.Data.MySqlClient;

namespace FoxBridge
{
    public class MySqlBulkImporter
    {
        public int BulkLoad(string csvPath, string tableName, MySqlConnection conn)
        {
          
            using (var cmd = new MySqlCommand($"TRUNCATE TABLE {tableName}", conn))
            {
                cmd.ExecuteNonQuery();
            }
             
            var loader = new MySqlBulkLoader(conn)
            {
                FileName = csvPath,
                TableName = tableName,
                Local = true,
                FieldTerminator = "\t",
                LineTerminator = "\n",   
                NumberOfLinesToSkip = 0,
                CharacterSet = "utf8mb4",

               
                ConflictOption = MySqlBulkLoaderConflictOption.Ignore
            };



            return loader.Load();
        }
    }
}