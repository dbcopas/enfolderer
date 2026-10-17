using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Enfolderer.Ai.Contracts;
using Enfolderer.App.Collection;
using Enfolderer.App.Imaging;
using Microsoft.Data.Sqlite;

namespace Enfolderer.App.Tests;

public static class ScanOwnershipTests
{
    public static int RunAll()
    {
        var failures = 0;
        void Check(bool condition, string message)
        {
            if (condition) return;
            failures++;
            Console.Error.WriteLine("Scan ownership: " + message);
        }
        var folder = Path.Combine(Path.GetTempPath(), "enfolderer-scan-owned-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using (var main = new SqliteConnection($"Data Source={Path.Combine(folder, "mainDb.db")};Pooling=False"))
            {
                main.Open();
                using var command = main.CreateCommand();
                command.CommandText = """
                    CREATE TABLE Cards(id INTEGER, name TEXT, edition TEXT, collectorNumberValue TEXT, MtgsId INTEGER, Qty INTEGER);
                    INSERT INTO Cards VALUES
                    (1,'Test Card','aaa','001',101,99),
                    (2,'Test Card','bbb','1',102,99),
                    (3,'Test Card','aaa','1a',NULL,3),
                    (4,'Test Card','aaa','2',NULL,4),
                    (5,'Double Front','aaa','3',103,0),
                    (6,'Double Front','aaa','4',104,0),
                    (7,'Missing Card','aaa','5',105,99),
                    (8,'Test Card','aaa','001',101,99),
                    (9,'Test Card','aaa','6',106,2),
                    (10,'Test Card','aaa','',NULL,100),
                    (11,'Galadriel of Lothlórien','ltr','206',NULL,2),
                    (12,'Galadriel of Lothlórien','ltr','446',NULL,3),
                    (13,'Horn of Valhalla (Ysgard''s Call)','clb','26',NULL,1),
                    (14,'Horn of Valhalla (Ysgard''s Call)','clb','555',NULL,4),
                    (15,'Horn of Valhalla // Different Spell','zzz','26',NULL,50);
                    """;
                command.ExecuteNonQuery();
            }
            using (var collection = new SqliteConnection($"Data Source={Path.Combine(folder, "mtgstudio.collection")};Pooling=False"))
            {
                collection.Open();
                using var command = collection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE CollectionCards(CardId INTEGER, Qty INTEGER);
                    INSERT INTO CollectionCards VALUES (101,1),(101,1),(102,10),(103,1),(104,2),(105,0);
                    """;
                command.ExecuteNonQuery();
            }
            var index = new ScanOwnershipIndex(folder);
            var card = new IdentifiedCard
            {
                Game = CardGames.Magic, Set = "AAA", CollectorNumber = "1", Name = "Test Card",
                Prices = new CardPrices { Layout = "normal", CardName = "Test Card" },
                Quad = new CardQuad([new(40, 40), new(760, 40), new(760, 1060), new(40, 1060)])
            };
            var owned = index.Resolve(card);
            Check(owned.PrintingQuantity == 2 && owned.NameQuantity == 121 && !owned.IsMissing,
                "exact printing is separate from same-name total, repeated mappings are counted once, collection overrides stale main qty");
            Check(index.Resolve(card with { CollectorNumber = "1a" }).PrintingQuantity == 3,
                "collector suffix remains distinct and custom quantity comes from mainDb");
            Check(index.Resolve(card with { CollectorNumber = "6" }).PrintingQuantity == 2,
                "mapped card absent from collection falls back to mainDb");
            var prepared = card with { Confidence = 0.1,
                Prices = new CardPrices { Layout = "prepare", CardName = "Test Card // Prepared Spell" } };
            Check(index.Resolve(prepared) is { PrintingQuantity: 2, RequiredQuantity: 1, IsMissing: false },
                "prepare uses one copy and ownership is independent of confidence");
            Check(index.Resolve(card with { Name = null,
                Prices = new CardPrices { Layout = "normal", CardName = "Unrelated catalogue name" } }) == owned,
                "set and collector number determine ownership without a scanned or catalogue name");
            var galadriel = card with { Name = "Galadriel of Lothlorien", Set = "LTR", CollectorNumber = "206",
                Prices = new CardPrices { Layout = "normal", CardName = "Galadriel of Lothlórien" } };
            Check(index.Resolve(galadriel) is { PrintingQuantity: 2, NameQuantity: 5, IsMissing: false },
                "diacritics in scanned and catalogue names cannot block the local printing or same-name total");
            var horn = card with { Name = "Horn of Valhalla", Set = "CLB", CollectorNumber = "26",
                Prices = new CardPrices { Layout = "adventure", CardName = "Horn of Valhalla // Ysgard's Call" } };
            Check(index.Resolve(horn) is { PrintingQuantity: 1, NameQuantity: 5, RequiredQuantity: 1, IsMissing: false },
                "adventure names come from the local printing without matching external punctuation or merging different spells");
            var missing = card with { Name = "Missing Card", CollectorNumber = "5",
                Prices = new CardPrices { Layout = "normal", CardName = "Missing Card" } };
            Check(index.Resolve(missing).IsMissing && index.Resolve(missing).OverlayText == "",
                "zero in collection wins over stale main quantity; missing card has no quantity text");
            var dfc = card with { Name = "Double Front", CollectorNumber = "3",
                Prices = new CardPrices { Layout = "modal_dfc", CardName = "Double Front // Double Back" } };
            Check(index.Resolve(dfc).IsMissing && index.Resolve(dfc).OverlayText == ""
                && !index.Resolve(dfc with { CollectorNumber = "4" }).IsMissing,
                "physical double-faced cards require two exact-printing copies and suppress quantity when short");
            Check(ScanOwnershipIndex.RequiredQuantity("transform") == 2
                && ScanOwnershipIndex.RequiredQuantity("battle") == 2
                && ScanOwnershipIndex.RequiredQuantity("split") == 1
                && ScanOwnershipIndex.RequiredQuantity("adventure") == 1,
                "two-sided cards differ from two spells on one front");
            foreach (var invalid in new[] { card with { CollectorNumber = "999" }, card with { CollectorNumber = "0" },
                card with { Prices = null } })
            {
                try { index.Resolve(invalid); Check(false, "unavailable ownership must be explicit"); }
                catch (InvalidOperationException) { }
            }
            var path = Path.Combine(folder, "preview.png");
            void SaveBlank()
            {
                var bitmap = new RenderTargetBitmap(800, 1300, 96, 96, PixelFormats.Pbgra32);
                var visual = new DrawingVisual();
                using (var drawing = visual.RenderOpen())
                    drawing.DrawRectangle(Brushes.DarkGreen, null, new Rect(0, 0, 800, 1300));
                bitmap.Render(visual);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(path);
                encoder.Save(output);
            }
            byte[] ReadPixel(int x, int y)
            {
                using var input = File.OpenRead(path);
                var bitmap = BitmapFrame.Create(input, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
                byte[] pixel = new byte[4];
                converted.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
                return pixel;
            }
            var result = new ScanResultDocument { ImageWidth = 800, ImageHeight = 1100, Cards = [missing] };
            SaveBlank();
            var before = ReadPixel(40, 100);
            var panelBefore = ReadPixel(60, 360);
            var legend = ReadPixel(40, 1200);
            Check(ScanOwnershipRenderer.Render(path, result, index).Count == 0
                && ReadPixel(40, 100).SequenceEqual(before)
                && ReadPixel(60, 360)[2] > panelBefore[2]
                && ReadPixel(60, 480).SequenceEqual(before)
                && ReadPixel(40, 1200).SequenceEqual(legend),
                "missing card gets a pink price-overlay tint, no quantity or frame, and an unchanged price legend");
            SaveBlank();
            Check(ScanOwnershipRenderer.Render(path, result with { Cards = [card] }, index).Count == 0
                && ReadPixel(40, 100).SequenceEqual(before)
                && !ReadPixel(60, 480).SequenceEqual(before),
                "sufficient card gets a quantity panel and no frame");
            SaveBlank();
            Check(ScanOwnershipRenderer.Render(path, result with { Cards = [dfc] }, index).Count == 0
                && ReadPixel(40, 100).SequenceEqual(before)
                && ReadPixel(60, 360)[2] > panelBefore[2],
                "one-copy double-faced printing gets a pink overlay without a frame");
            SaveBlank();
            Check(ScanOwnershipRenderer.Render(path, result with { Cards = [prepared] }, index).Count == 0
                && ReadPixel(40, 100).SequenceEqual(before)
                && !ReadPixel(60, 480).SequenceEqual(before),
                "owned prepare card gets quantity despite low confidence");
            foreach (var differentlyNamed in new[] { galadriel, horn })
            {
                SaveBlank();
                Check(ScanOwnershipRenderer.Render(path, result with { Cards = [differentlyNamed] }, index).Count == 0
                    && ReadPixel(40, 100).SequenceEqual(before)
                    && ReadPixel(60, 360).SequenceEqual(panelBefore)
                    && !ReadPixel(60, 480).SequenceEqual(before),
                    "diacritic and adventure-name differences keep owned overlays untinted with quantity text");
            }
            foreach (var unresolved in new[] { card with { Prices = null },
                card with { CollectorNumber = "999" }, card with { CollectorNumber = null },
                card with { Prices = new CardPrices { Layout = "future_layout" } } })
            {
                SaveBlank();
                Check(ScanOwnershipRenderer.Render(path, result with { Cards = [unresolved] }, index).Count == 1
                    && ReadPixel(40, 100).SequenceEqual(before)
                    && ReadPixel(60, 360)[2] > panelBefore[2]
                    && ReadPixel(60, 360)[1] > panelBefore[1]
                    && !ReadPixel(60, 480).SequenceEqual(before)
                    && ReadPixel(40, 1200).SequenceEqual(legend),
                    "unresolved ownership gets an amber overlay, unknown quantity panel, no frame and explicit warning");
            }
            SaveBlank();
            ScanOwnershipRenderer.Render(path, result with { Cards = [card] }, null);
            Check(ReadPixel(40, 100).SequenceEqual(before)
                && ReadPixel(60, 360)[2] > panelBefore[2]
                && ReadPixel(60, 360)[1] > panelBefore[1]
                && !ReadPixel(60, 480).SequenceEqual(before),
                "unavailable local inventory also marks ownership unknown");

            TestProvidedDatabases(Check);
        }
        finally { Directory.Delete(folder, recursive: true); }
        return failures;
    }

    private static void TestProvidedDatabases(Action<bool, string> check)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Enfolderer.sln")))
            directory = directory.Parent;
        if (directory is null) return; // Published packages have no repository training fixtures.
        var folder = Path.Combine(directory.FullName, "training", "testing dbs");
        var paths = new[] { Path.Combine(folder, "mainDb.db"), Path.Combine(folder, "mtgstudio.collection") };
        var before = paths.Select(path => SHA256.HashData(File.ReadAllBytes(path))).ToArray();
        var index = new ScanOwnershipIndex(folder);
        var emeritus = index.Resolve(new IdentifiedCard
        {
            Game = CardGames.Magic, Name = "Emeritus of Woe", Set = "SOS", CollectorNumber = "80",
            Confidence = 0.1,
            Prices = new CardPrices { Layout = "prepare", CardName = "Emeritus of Woe // Demonic Tutor" }
        });
        check(emeritus.RequiredQuantity == 1, "provided Emeritus of Woe SOS 80 resolves prepare ownership");
        foreach (var (set, number, scannedName, catalogueName, layout) in new[]
        {
            ("ltr", "206", "Galadriel of Lothlorien", "Galadriel of Lothlórien", "normal"),
            ("clb", "26", "Horn of Valhalla", "Horn of Valhalla // Ysgard's Call", "adventure")
        })
        {
            var external = new IdentifiedCard
            {
                Game = CardGames.Magic, Name = scannedName, Set = set, CollectorNumber = number,
                Prices = new CardPrices { Layout = layout, CardName = catalogueName }
            };
            var ownership = index.Resolve(external);
            check(ownership == index.Resolve(external with { Name = null,
                Prices = new CardPrices { Layout = layout, CardName = "Different external spelling" } })
                && ownership.NameQuantity >= ownership.PrintingQuantity,
                $"provided {set} {number} ownership is independent of scanned and catalogue spelling");
        }
        using var database = new SqliteConnection($"Data Source={paths[0]};Mode=ReadOnly;Pooling=False");
        database.Open();
        using var command = database.CreateCommand();
        command.CommandText = """
            SELECT name, edition, collectorNumberValue FROM Cards
            WHERE length(trim(name)) > 0 AND length(trim(edition)) > 0
              AND length(trim(collectorNumberValue)) > 0 LIMIT 100
            """;
        using var rows = command.ExecuteReader();
        var checkedRows = 0;
        while (rows.Read())
        {
            var name = rows.GetString(0);
            var ownership = index.Resolve(new IdentifiedCard
            {
                Game = CardGames.Magic, Name = name, Set = rows.GetString(1),
                CollectorNumber = rows.GetValue(2).ToString(),
                Prices = new CardPrices { Layout = "normal", CardName = name }
            });
            check(ownership.NameQuantity >= ownership.PrintingQuantity, "provided fixture exact quantity never exceeds same-name total");
            checkedRows++;
        }
        check(checkedRows > 0, "provided testing databases resolve real catalogue entries");
        check(paths.Select((path, i) => SHA256.HashData(File.ReadAllBytes(path)).SequenceEqual(before[i])).All(x => x),
            "testing databases remain byte-for-byte unchanged");
    }
}
