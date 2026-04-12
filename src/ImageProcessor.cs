using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using OpenCvSharp;

namespace Garden
{
    public static class ImageProcessor
    {
        public static Rect? FindStableRoi(Mat frame, string imagePath)
        {
            using var img2 = Cv2.ImRead(imagePath, ImreadModes.Color);
            if (img2.Empty() || frame.Empty())
            {
                return null;
            }

            if (img2.Size() != frame.Size())
            {
                Cv2.Resize(img2, img2, frame.Size());
            }
            using var diff = new Mat();
            Cv2.Absdiff(frame, img2, diff);

            using var gray = new Mat();
            Cv2.CvtColor(diff, gray, ColorConversionCodes.BGR2GRAY);
            Cv2.BitwiseNot(gray, gray);
            Cv2.GaussianBlur(gray, gray, new Size(11, 11), 0);
            Cv2.Threshold(gray, gray, 200, 255, ThresholdTypes.Binary);

            Cv2.FindContours(gray, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
            if (contours.Length > 0)
            {
                var largest = contours.OrderByDescending(c => Cv2.ContourArea(c)).First();
                return Cv2.BoundingRect(largest);
            }
            return null;
        }
    }
}
