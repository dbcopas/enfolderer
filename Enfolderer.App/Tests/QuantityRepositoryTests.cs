using System;
using System.IO;
using Microsoft.Data.Sqlite;
using Enfolderer.App.Collection;
using Enfolderer.App.Core.Abstractions;

namespace Enfolderer.App.Tests;

/// <summary>
/// Characterization tests for quantity persistence repository (custom vs standard paths, update vs insert).
/// Provides baseline safety before further refactors of persistence logic.
/// </summary>
public static class QuantityRepositoryTests
{
    private static void Log(bool ok, ref int failures, string msg, string logPath)
    { if(!ok){ failures++; try { File.AppendAllText(logPath, "FAIL "+msg+"\n"); } catch {} } else { try { File.AppendAllText(logPath, "OK "+msg+"\n"); } catch {} } }

    public static int RunAll()
    {
        int failures = 0;
        string log = Path.Combine(Path.GetTempPath(), "enfolderer_qtyrepo_tests.txt");
        try { File.WriteAllText(log, "START\n"); } catch {}
        var collection = new CardCollectionData();
        var repo = new CollectionRepository(collection); // implements IQuantityRepository
        IQuantityRepository qrepo = repo;
        string tempDir = Path.Combine(Path.GetTempPath(), "enfolderer_qtyrepo_"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string mainDb = Path.Combine(tempDir, "mainDb.db");
        string collectionFile = Path.Combine(tempDir, "mtgstudio.collection");

        // Prepare mainDb with Cards table (custom path)
        try
        {
            using var con = new SqliteConnection($"Data Source={mainDb}");
            con.Open();
            using (var cmd = con.CreateCommand()) { cmd.CommandText = "CREATE TABLE Cards (id INTEGER PRIMARY KEY, Qty INTEGER)"; cmd.ExecuteNonQuery(); }
            using (var cmd = con.CreateCommand()) { cmd.CommandText = "INSERT INTO Cards (id,Qty) VALUES (1,0)"; cmd.ExecuteNonQuery(); }
        }
        catch (Exception ex) { Log(false, ref failures, "Setup mainDb failed: "+ex.Message, log); }
        // Update existing custom card quantity
        int? customResult = qrepo.UpdateCustomCardQuantity(mainDb, 1, 5, qtyDebug:false);
        Log(customResult==5, ref failures, "Custom card update to 5", log);

        // Prepare collection file with CollectionCards table (standard path)
        try
        {
            using var con = new SqliteConnection($"Data Source={collectionFile}");
            con.Open();
            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText = @"CREATE TABLE CollectionCards (
CardId INTEGER PRIMARY KEY, Qty INTEGER, Used INT, BuyAt REAL, SellAt REAL, Price REAL,
Needed INT, Excess INT, Target INT, ConditionId INT, Foil INT, Notes TEXT, Storage TEXT,
DesiredId INT, [Group] TEXT, PrintTypeId INT, Buy INT, Sell INT, Added TEXT)";
                cmd.ExecuteNonQuery();
            }
            using (var cmd = con.CreateCommand()) { cmd.CommandText = "INSERT INTO CollectionCards (CardId,Qty,Used,BuyAt,SellAt,Price,Needed,Excess,Target,ConditionId,Foil,Notes,Storage,DesiredId,[Group],PrintTypeId,Buy,Sell,Added) VALUES (5,1,0,0,0,0,0,0,0,0,0,'','',0,'',1,0,0,'2024-01-01 00:00:00')"; cmd.ExecuteNonQuery(); }
        }
        catch (Exception ex) { Log(false, ref failures, "Setup collection file failed: "+ex.Message, log); }
        // Update existing standard card row
        int? stdUpdate = qrepo.UpsertStandardCardQuantity(collectionFile, 5, 3, qtyDebug:false);
        Log(stdUpdate==3, ref failures, "Standard card update existing row to 3", log);
        // Insert new standard card row (CardId=9)
        int? stdInsert = qrepo.UpsertStandardCardQuantity(collectionFile, 9, 2, qtyDebug:false);
        Log(stdInsert==2, ref failures, "Standard card insert new row with qty 2", log);
        // Missing file scenario returns null
        int? missingResult = qrepo.UpdateCustomCardQuantity(Path.Combine(tempDir, "does_not_exist.db"), 2, 1, qtyDebug:false);
        Log(missingResult==null, ref failures, "Custom update missing file returns null", log);

        try { File.AppendAllText(log, "DONE failures="+failures+"\n"); } catch {}
        return failures;
    }
}
