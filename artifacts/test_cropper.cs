using System;
using System.IO;
using Enfolderer.Ai.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

class Program
{
    static void Main()
    {
        using var img4 = Image.Load<Rgba32>("training/04/image.jpg");
        var detected4 = CardArrangementDetector.Detect(img4);
        Console.WriteLine($"04 detected: {detected4.Count} cards");
        Directory.CreateDirectory("artifacts/test-crops-04");
        for (int i = 0; i < detected4.Count; i++)
        {
            using var inStream = File.OpenRead("training/04/image.jpg");
            using var outStream = File.Create($"artifacts/test-crops-04/card-{i:D3}.png");
            PerspectiveCropper.CropToPng(inStream, detected4[i].Quad, outStream);
        }

        using var img5 = Image.Load<Rgba32>("training/05/image.jpg");
        var detected5 = CardArrangementDetector.Detect(img5);
        Console.WriteLine($"05 detected: {detected5.Count} cards");
        Directory.CreateDirectory("artifacts/test-crops-05");
        for (int i = 0; i < detected5.Count; i++)
        {
            using var inStream = File.OpenRead("training/05/image.jpg");
            using var outStream = File.Create($"artifacts/test-crops-05/card-{i:D3}.png");
            PerspectiveCropper.CropToPng(inStream, detected5[i].Quad, outStream);
        }
    }
}
