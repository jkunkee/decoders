using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using LabNation.Interfaces;

// There are two decent documents about decoder authoring:
//http://wiki.lab-nation.com/index.php/Creating_your_own_Protocol_Decoder
//https://github.com/labnation/decoders/blob/master/README.md
/*
copy /Y "D:\CrashPlan\Video Capture Device\decoders\DecoderMDIO\bin\Debug\DecoderMDIO.dll" C:\Users\Jon\Documents\LabNation\Plugins
*/

namespace LabNation.Decoders
{
    [Export(typeof(IProcessor))]
    public class DecoderMDIO : IDecoder
    {
        public DecoderDescription Description
        {
            get
            {
                return new DecoderDescription()
                {
                    Name = "MDIO Decoder",
                    ShortName = "MDIO",
                    Author = "Jon Kunkee",
                    VersionMajor = 0,
                    VersionMinor = 1,
                    Description = "Decoder for (G)MII-compliant network devices' MDIO control protocol",
                    InputWaveformTypes = new Dictionary<string, Type>()
                    {
                        { "MDC", typeof(bool)},
                        { "MDIO", typeof(bool)}
                    },
                    InputWaveformExpectedToggleRates = new Dictionary<string, ToggleRate>()
                    {
                        { "MDC", ToggleRate.High},
                        { "MDIO", ToggleRate.Medium}
                    },
                    Parameters = null
                };
            }
        }

        private int SliceToSignedInt(bool[] arr, int startIdx, int endIdx) {
            int val = 0;

            if (startIdx > endIdx || startIdx < 0 || endIdx >= arr.Length)
            {
                Console.Beep();
                return 0;
            }

            for (int idx = startIdx; idx <= endIdx; idx++)
            {
                val <<= 1;
                if (arr[idx])
                {
                    val |= 1;
                }
            }

            return val;
        }

        public DecoderOutput[] Process(Dictionary<string, Array> inputWaveforms, Dictionary<string, object> parameters, double samplePeriod)
        {
            //name input waveforms for easier usage
            bool[] MDC = (bool[])inputWaveforms["MDC"];
            bool[] MDIO = (bool[])inputWaveforms["MDIO"];

            //initialize output structure
            List<DecoderOutput> decoderOutputList = new List<DecoderOutput>();

            // Per https://en.wikipedia.org/wiki/Management_Data_Input/Output
            //    When the MAC drives the MDIO line, it has to guarantee a stable value 10 ns (setup time)
            //    before the rising edge of the clock MDC. Further, MDIO has to remain stable 10 ns (hold time)
            //    after the rising edge of MDC.
            //
            //    When the PHY drives the MDIO line, the PHY has to provide the MDIO signal between 0 and 300 ns
            //    after the rising edge of the clock.
            //    Hence, with a minimum clock period of 400 ns(2.5 MHz maximum clock rate) the MAC can safely
            //    sample MDIO during the second half of the low cycle of the clock.
            //
            // I realize the 400ns minimum clock period isn't clearly required by the Wikipedia text, but it
            // is a valid statement per the spec. 802.3 FTW

            double minimumClockPeriod = 400.0 / 1000000000.0;
            int bitSampleLength = System.Convert.ToInt32(minimumClockPeriod / samplePeriod) - 1;

            // slave-originated bits are valid from 300-400ns after the rising edge. Sample at approximately
            // 350ns give or take one sample (forced below to be at most 20ns).
            int readSampleOffset = System.Convert.ToInt32((minimumClockPeriod / samplePeriod) * 7 / 8);

            // SmartScope calls this function many times. Understanding that plenitude of calls is key to
            // writing a successful decoder.
            // For fast decoding and thumbnail work, it hands in 2048-sample arrays. It only hands in full
            // data buffers when data collection stops (and you get the last one).
            // Work around this by enforcing minimum length of 64 MDIO bit segments.
            if (64.0 * bitSampleLength > MDC.Length)
            {
                decoderOutputList.Add(new DecoderOutputEvent(1 * MDC.Length / 10, 9 * MDC.Length / 10, DecoderOutputColor.Orange, "Viewport fast decode not supported"));
                return decoderOutputList.ToArray();
            }

            // If this inequality holds, then when a clock rising edge is detected on MDC the corresponding
            // samples from MDIO can be used as valid data (for master-originated bits).
            // Empirically, samplePeriod == 10ns is common on the SmartScope (at 100Msps in Sys menu)
            double maximumSamplePeriod = 20.0 / 1000000000.0;

            if (samplePeriod > maximumSamplePeriod)
            {
                decoderOutputList.Add(new DecoderOutputEvent(4 * MDC.Length / 10, 7 * MDC.Length / 10, DecoderOutputColor.Orange, "sample period too large"));
                return decoderOutputList.ToArray();
            }

            // Rely on input prep to filter bouncy edges - TODO verify assumption

            // Start at i=1 so i-1 is always a valid index (for edge detection)
            for (int startSampleIndex = 1; startSampleIndex < MDC.Length; startSampleIndex++)
            {
                bool foundFirstRisingEdge = !MDC[startSampleIndex - 1] && MDC[startSampleIndex];

                // start decoding at a rising edge
                if (!foundFirstRisingEdge)
                {
                    continue;
                }

                // preamble starts with 32 ones, so if bit 0 isn't 1 then keep looking
                if (!MDIO[startSampleIndex])
                {
                    continue;
                }

                // Convert the next 64 clocks into bits

                // Full MDIO messages are 64 bits long. Store an index for the start of each bit.
                int[] bitIndices;
                bool[] bitValues;
                int bitIndex;

                bitIndices = new int[64];
                bitValues = new bool[64];
                bitIndex = 0;
                bool isRead = false; // govern how to sample data bits

                for (int signalSample = startSampleIndex; signalSample < MDC.Length && bitIndex < 64; signalSample++)
                {
                    // outer for loop guarantees this will be true for the first iteration
                    bool foundRisingEdge = !MDC[signalSample - 1] && MDC[signalSample];

                    if (!foundRisingEdge)
                    {
                        continue;
                    }

                    // read the bit
                    if (bitIndex < 46 || !isRead)
                    {
                        bitIndices[bitIndex] = signalSample;
                        bitValues[bitIndex] = MDIO[signalSample];
                    }
                    else
                    {
                        // bitIndex >= 46 && isRead
                        // Ensure it's safe to sample at T+350ns
                        if (signalSample + readSampleOffset < MDC.Length)
                        {
                            bitIndices[bitIndex] = signalSample;
                            bitValues[bitIndex] = MDIO[signalSample + readSampleOffset];
                        }
                        else
                        {
                            break;
                        }
                    }

                    bitIndex++;

                    // Next, emit DecoderEvents that correspond with that has been decoded so far.
                    // If the preamble isn't valid, don't emit anything.
                    // N.B. THIS WILL BE VERY HARD TO UNDERSTAND WITHOUT A DIAGRAM OF ALL THE BITS
                    // IN AN MDIO MESSAGE AND THEIR 0-BASED INDICES. Oh, and the fact that bitIndex
                    // gets incremented before the decode section.

                    // preamble starts with 32 ones
                    if (bitIndex <= 32)
                    {
                        if (bitValues[bitIndex - 1] != true)
                        {
                            break;
                        }
                    }

                    // preamble ends in start bits "01"
                    if (bitIndex == 34)
                    {
                        if (!(bitValues[bitIndex - 2] == false && bitValues[bitIndex - 1] == true))
                        {
                            break;
                        }

                        // preamble has been found!
                        decoderOutputList.Add(
                            new DecoderOutputEvent(
                                bitIndices[0],
                                bitIndices[31] + bitSampleLength,
                                DecoderOutputColor.DarkBlue,
                                "PREAMBLE"
                                ));
                        decoderOutputList.Add(
                            new DecoderOutputEvent(
                                bitIndices[32],
                                bitIndices[33] + bitSampleLength,
                                DecoderOutputColor.Blue,
                                "S"
                                ));
                    }

                    // Op code
                    if (bitIndex == 36)
                    {
                        string val;
                        // Read is 1 0
                        if (bitValues[bitIndex - 2] == true && bitValues[bitIndex - 1] == false)
                        {
                            isRead = true;
                            val = "R";
                        }
                        // Write is 0 1
                        else if (bitValues[bitIndex - 2] == false && bitValues[bitIndex - 1] == true)
                        {
                            isRead = false;
                            val = "W";
                        }
                        // Some extensions allow other values
                        else
                        {
                            val = "";
                            if (bitValues[bitIndex - 2])
                            {
                                val += "1";
                            }
                            else
                            {
                                val += "0";
                            }
                            if (bitValues[bitIndex - 1])
                            {
                                val += "1";
                            }
                            else
                            {
                                val += "0";
                            }
                        }
                        decoderOutputList.Add(new DecoderOutputEvent(bitIndices[34], bitIndices[35] + bitSampleLength, DecoderOutputColor.Green, val));
                    }

                    // PHY address
                    if (bitIndex == 41)
                    {
                        int phyAddr = SliceToSignedInt(bitValues, 36, 40);
                        decoderOutputList.Add(new DecoderOutputEvent(bitIndices[36], bitIndices[40] + bitSampleLength, DecoderOutputColor.DarkPurple, "10'" + phyAddr));
                    }

                    // Register address
                    if (bitIndex == 46)
                    {
                        int regAddr = SliceToSignedInt(bitValues, 41, 45);
                        decoderOutputList.Add(new DecoderOutputEvent(bitIndices[41], bitIndices[45] + bitSampleLength, DecoderOutputColor.DarkBlue, "10'" + regAddr));
                    }

                    // turnaround bits
                    if (isRead)
                    {
                        if (bitIndex == 48)
                        {
                            string val;
                            val = "TA";
                            decoderOutputList.Add(new DecoderOutputEvent(bitIndices[46], bitIndices[47] + bitSampleLength, DecoderOutputColor.DarkRed, val));
                        }
                    }
                    else
                    {
                        // Looks like only one TA bit on reads. Weird.
                        if (bitIndex == 47)
                        {
                            string val;
                            val = "";
                            val += bitValues[46] ? "1" : "0";
                            decoderOutputList.Add(new DecoderOutputEvent(bitIndices[46], bitIndices[46] + bitSampleLength, DecoderOutputColor.DarkRed, val));
                        }
                    }
                    if ((bitIndex == 64 && !isRead) || (isRead && bitIndex == 63))
                    {
                        string val = "";
                        for (int idx = 0; idx < 16; idx++)
                        {
                            if (isRead)
                            {
                                val += bitValues[47 + idx] ? "1" : "0";
                            }
                            else
                            {
                                val += bitValues[48 + idx] ? "1" : "0";
                            }
                        }
                        decoderOutputList.Add(new DecoderOutputEvent(bitIndices[48], bitIndices[63] + bitSampleLength, DecoderOutputColor.Blue, val));
                    }
                } // for decode
            } // for sample array

            return decoderOutputList.ToArray();
        }
    }
}
