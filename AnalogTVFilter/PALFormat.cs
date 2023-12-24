﻿using System.Numerics;

namespace AnalogTVFilter
{
    // The PAL format, used in Europe, etc.
    public class PALFormat : AnalogFormat
    {
        public PALFormat() : base(0.299, // R to Y
                                  0.587, // G to Y
                                  0.114, // B to Y
                                  0.436, // U maximum
                                  0.615, // V maximum
                                  0.0, //  Chroma conversion phase relative to YUV (zero in this case because PAL uses YUV exactly)
                                  5e+6, // Main bandwidth
                                  0.75e+6, // Side bandwidth
                                  1.3e+6, // Color bandwidth lower part
                                  0.6e+6, // Color bandwidth upper part
                                  4433618.75, // Color subcarrier frequency
                                  625, // Total scanlines
                                  576, // Visible scanlines
                                  50.0, // Nominal framerate
                                  5.195e-5, // Active video time
                                  true) // Interlaced?
        { }

        public override ImageData Decode(double[] signal, int activeWidth, double crosstalk = 0.0, double resonance = 1.0, double scanlineJitter = 0.0, int channelFlags = 0x7)
        {
            int[] activeSignalStarts = new int[videoScanlines]; // Start points of the active parts
            byte R = 0;
            byte G = 0;
            byte B = 0;
            double Y = 0.0;
            double U = 0.0;
            double V = 0.0;
            int polarity = 0;
            int pos = 0;
            int posdel = 0;
            double sigNum = 0.0;
            double sampleRate = signal.Length / frameTime;
            double blendStr = 1.0 - crosstalk;
            bool inclY = ((channelFlags & 0x1) == 0) ? false : true;
            bool inclU = ((channelFlags & 0x2) == 0) ? false : true;
            bool inclV = ((channelFlags & 0x4) == 0) ? false : true;

            Complex[] signalFT = MathUtil.FourierTransform(signal, 1);
            signalFT = MathUtil.BandPassFilter(signalFT, sampleRate, (mainBandwidth - sideBandwidth) / 2.0, mainBandwidth + sideBandwidth, resonance); //Restrict bandwidth to the actual broadcast bandwidth
            Complex[] colorSignalFT = MathUtil.BandPassFilter(signalFT, sampleRate, ((chromaBandwidthUpper - chromaBandwidthLower) / 2.0) + chromaCarrierFrequency, chromaBandwidthLower + chromaBandwidthUpper, resonance, blendStr); //Extract color information
            colorSignalFT = MathUtil.ShiftArrayInterp(colorSignalFT, ((((chromaBandwidthUpper - chromaBandwidthLower) / 2.0) + chromaCarrierFrequency + 2404.0) / sampleRate) * colorSignalFT.Length); // apologies for the fudge factor
            Complex[] USignalIFT = MathUtil.InverseFourierTransform(colorSignalFT);
            double[] USignal = new double[signal.Length];
            double[] VSignal = new double[signal.Length];
            signalFT = MathUtil.NotchFilter(signalFT, sampleRate, ((chromaBandwidthUpper - chromaBandwidthLower) / 2.0) + chromaCarrierFrequency, chromaBandwidthLower + chromaBandwidthUpper, resonance, blendStr);
            Complex[] finalSignal = MathUtil.InverseFourierTransform(signalFT);

            for (int i = 0; i < signal.Length; i++)
            {
                signal[i] = 1.0 * finalSignal[finalSignal.Length - 1 - i].Real;
                USignal[i] = -2.0 * USignalIFT[finalSignal.Length - 1 - i].Imaginary;
                VSignal[i] = 2.0 * USignalIFT[finalSignal.Length - 1 - i].Real;
            }

            ImageData writeToSurface = new ImageData();
            writeToSurface.Width = activeWidth;
            writeToSurface.Height = videoScanlines;
            writeToSurface.Data = new byte[activeWidth * videoScanlines * 4];

            for (int i = 0; i < videoScanlines; i++) // Where the active signal starts
            {
                activeSignalStarts[i] = (int)((((double)i * (double)signal.Length) / (double)videoScanlines) + ((scanlineTime - realActiveTime) / (2 * realActiveTime)) * activeWidth);
            }

            for (int i = 0; i < videoScanlines; i++) // Account for phase alternation
            {
                if ((i % 2) == 0) continue;
                pos = activeSignalStarts[i];
                posdel = activeSignalStarts[i - 1];
                for (int j = 0; j < writeToSurface.Width; j++)
                {
                    USignal[pos] = (USignal[posdel] + USignal[pos]) / 2.0;
                    VSignal[pos] = (VSignal[posdel] - VSignal[pos]) / 2.0;
                    USignal[posdel] = USignal[pos];
                    VSignal[posdel] = VSignal[pos];
                    pos++;
                    posdel++;
                }
            }

            byte[] surfaceColors = writeToSurface.Data;
            int currentScanline;
            Random rng = new Random();
            int curjit = 0;
            for (int i = 0; i < videoScanlines; i++)
            {
                if (i * 2 >= videoScanlines) // Simulate interlacing
                {
                    polarity = 1;
                }
                currentScanline = isInterlaced ? (i * 2 + polarity) % videoScanlines : i;
                curjit = (int)(scanlineJitter * 2.0 * (rng.NextDouble() - 0.5) * activeWidth);
                pos = activeSignalStarts[i] + curjit;

                for (int j = 0; j < writeToSurface.Width; j++) // Decode active signal region only
                {
                    Y = inclY ? signal[pos] : 0.5;
                    U = inclU ? USignal[pos] : 0.0;
                    V = inclV ? VSignal[pos] : 0.0;
                    R = (byte)(MathUtil.Clamp(Math.Pow(YUVtoRGBConversionMatrix[0] * Y + YUVtoRGBConversionMatrix[2] * V, 0.357), 0.0, 1.0) * 255.0);
                    G = (byte)(MathUtil.Clamp(Math.Pow(YUVtoRGBConversionMatrix[3] * Y + YUVtoRGBConversionMatrix[4] * U + YUVtoRGBConversionMatrix[5] * V, 0.357), 0.0, 1.0) * 255.0);
                    B = (byte)(MathUtil.Clamp(Math.Pow(YUVtoRGBConversionMatrix[6] * Y + YUVtoRGBConversionMatrix[7] * U, 0.357), 0.0, 1.0) * 255.0);
                    surfaceColors[(currentScanline * writeToSurface.Width + j) * 4 + 3] = 255;
                    surfaceColors[(currentScanline * writeToSurface.Width + j) * 4 + 2] = R;
                    surfaceColors[(currentScanline * writeToSurface.Width + j) * 4 + 1] = G;
                    surfaceColors[(currentScanline * writeToSurface.Width + j) * 4] = B;
                    pos++;
                }
            }
            return writeToSurface;
        }

        public override double[] Encode(ImageData surface)
        {
            // To get a good analog feel, we must limit the vertical resolution; the horizontal
            // resolution will be limited as we decode the distorted signal.
            int signalLen = (int)(surface.Width * videoScanlines * (scanlineTime / realActiveTime));
            int[] boundaryPoints = new int[videoScanlines + 1]; // Boundaries of the scanline signals
            int[] activeSignalStarts = new int[videoScanlines]; // Start points of the active parts
            double[] signalOut = new double[signalLen];
            double R = 0.0;
            double G = 0.0;
            double B = 0.0;
            double U = 0.0;
            double V = 0.0;
            double time = 0;
            int pos = 0;
            int polarity = 0;
            double phaseAlternate = 1.0; //Why this is called PAL in the first place
            int remainingSync = 0;
            double sampleTime = realActiveTime / (double)surface.Width;

            boundaryPoints[0] = 0; // Beginning of the signal
            boundaryPoints[videoScanlines] = signalLen; // End of the signal
            for (int i = 1; i < videoScanlines; i++) // Rest of the signal
            {
                boundaryPoints[i] = (i * signalLen) / videoScanlines;
            }

            boundPoints = boundaryPoints;

            for (int i = 0; i < videoScanlines; i++) // Where the active signal starts
            {
                activeSignalStarts[i] = (int)((((double)i * (double)signalLen) / (double)videoScanlines) + ((scanlineTime - realActiveTime) / (2 * realActiveTime)) * surface.Width) - boundaryPoints[i];
            }

            byte[] surfaceColors = surface.Data;
            int currentScanline;
            for (int i = 0; i < videoScanlines; i++)
            {
                if (i * 2 >= videoScanlines) // Simulate interlacing
                {
                    polarity = 1;
                }
                currentScanline = isInterlaced ? (i * 2 + polarity) % videoScanlines : i;
                if ((i % 2) == 1) //Do phase alternation
                {
                    phaseAlternate = -1.0;
                }
                else phaseAlternate = 1.0;
                for (int j = 0; j < activeSignalStarts[i]; j++) // Front porch, ignore sync signal because we don't see its results
                {
                    signalOut[pos] = 0.0;
                    pos++;
                    time = pos * sampleTime;
                }
                for (int j = 0; j < surface.Width; j++) // Active signal
                {
                    signalOut[pos] = 0f;
                    R = surfaceColors[(currentScanline * surface.Width + j) * 4 + 2] / 255.0;
                    G = surfaceColors[(currentScanline * surface.Width + j) * 4 + 1] / 255.0;
                    B = surfaceColors[(currentScanline * surface.Width + j) * 4] / 255.0;
                    R = Math.Pow(R, 2.8); // Gamma correction
                    G = Math.Pow(G, 2.8);
                    B = Math.Pow(B, 2.8);
                    U = RGBtoYUVConversionMatrix[3] * R + RGBtoYUVConversionMatrix[4] * G + RGBtoYUVConversionMatrix[5] * B; // Encode U and V
                    V = RGBtoYUVConversionMatrix[6] * R + RGBtoYUVConversionMatrix[7] * G + RGBtoYUVConversionMatrix[8] * B;
                    signalOut[pos] += RGBtoYUVConversionMatrix[0] * R + RGBtoYUVConversionMatrix[1] * G + RGBtoYUVConversionMatrix[2] * B; //Add luma straightforwardly
                    signalOut[pos] += U * Math.Sin(carrierAngFreq * time) + phaseAlternate * V * Math.Cos(carrierAngFreq * time); // Add chroma via QAM
                    pos++;
                    time = pos * sampleTime;
                }
                while (pos < boundaryPoints[i + 1]) // Back porch, ignore sync signal because we don't see its results
                {
                    signalOut[pos] = 0.0;
                    pos++;
                    time = pos * sampleTime;
                }
            }
            return signalOut;
        }
    }
}
