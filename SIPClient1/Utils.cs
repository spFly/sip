using Microsoft.Extensions.Logging;
using NAudio;
using NAudio.Wave;
using Serilog;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace SIPClient1
{
    public class Utils
    {
        private static WaveFormat _waveFormat = new WaveFormat(8000, 16, 1);  // PCMU format used by both input and output streams.
        private static int INPUT_SAMPLE_PERIOD_MILLISECONDS = 20;           // This sets the frequency of the RTP packets.
        private static readonly string SIP_USERNAME = "1772";
        private static readonly string SIP_PASSWORD = "aaa10800bd32115d86e548b8dfb21816";

        /// <summary>
        ///  Adds a console logger. Can be omitted if internal SIPSorcery debug and warning messages are not required.
        /// </summary>
        public static void AddConsoleLogger()
        {
            var loggerFactory = new Microsoft.Extensions.Logging.LoggerFactory();
            var loggerConfig = new LoggerConfiguration()
                .Enrich.FromLogContext()
                .MinimumLevel.Is(Serilog.Events.LogEventLevel.Debug)
                .WriteTo.Console()
                .CreateLogger();
            loggerFactory.AddSerilog(loggerConfig);
            SIPSorcery.Sys.Log.LoggerFactory = loggerFactory;
        }

        public static (WaveOutEvent, BufferedWaveProvider) GetAudioOutputDevice()
        {
            WaveOutEvent waveOutEvent = new WaveOutEvent();
            var waveProvider = new BufferedWaveProvider(_waveFormat);
            waveProvider.DiscardOnBufferOverflow = true;
            waveOutEvent.Init(waveProvider);
            waveOutEvent.Play();

            return (waveOutEvent, waveProvider);
        }

        /// <summary>
        /// Get the audio input device, e.g. microphone. The input device that will provide 
        /// audio samples that can be encoded, packaged into RTP and sent to the remote call party.
        /// </summary>
        public static WaveInEvent GetAudioInputDevice()
        {
            if (WaveInEvent.DeviceCount == 0)
            {
                throw new ApplicationException("No audio input devices available. No audio will be sent.");
            }
            else
            {
                WaveInEvent waveInEvent = new WaveInEvent();
                WaveFormat waveFormat = _waveFormat;
                waveInEvent.BufferMilliseconds = INPUT_SAMPLE_PERIOD_MILLISECONDS;
                waveInEvent.NumberOfBuffers = 1;
                waveInEvent.DeviceNumber = 0;
                waveInEvent.WaveFormat = waveFormat;

                return waveInEvent;
            }
        }


        /// <summary>
        /// Wires up the active RTP session to the speaker.
        /// </summary>
        /// <param name="rtpSession">The active RTP session receiving the remote party's RTP packets.</param>
        /// <param name="audioOutProvider">The audio buffer for the default system audio output device.</param>
        public static void PlayRemoteMedia(RTPMediaSession rtpSession, BufferedWaveProvider audioOutProvider)
        {
            if (rtpSession == null)
            {
                return;
            }

            rtpSession.OnRtpPacketReceived += (rtpPacket) =>
            {
                var sample = rtpPacket.Payload;
                for (int index = 0; index < sample.Length; index++)
                {
                    short pcm = NAudio.Codecs.MuLawDecoder.MuLawToLinearSample(sample[index]);
                    byte[] pcmSample = new byte[] { (byte)(pcm & 0xFF), (byte)(pcm >> 8) };
                    audioOutProvider.AddSamples(pcmSample, 0, 2);
                }
            };
        }


        /// <summary>
        /// Gets the call descriptor to allow an outgoing call to be placed.
        /// </summary>
        /// <param name="callUri">The URI to place the call to.</param>
        /// <param name="rtpSession">The RTP session that will be handling the RTP/RTCP packets for the call.</param>
        /// <returns>A call descriptor.</returns>
        public static SIPCallDescriptor GetCallDescriptor(string callUri)
        {
            // Create a call descriptor to place an outgoing call.
            SIPCallDescriptor callDescriptor = new SIPCallDescriptor(
                SIP_USERNAME,
                SIP_PASSWORD,
                callUri,
                $"sip:{SIP_USERNAME}@localhost",
                callUri,
                null, null, null,
                SIPCallDirection.Out,
                SDP.SDP_MIME_CONTENTTYPE,
                null,
                null);

            return callDescriptor;
        }


        /// <summary>
        /// Connects the RTP packets we receive to the speaker and sends RTP packets for microphone samples.
        /// </summary>
        /// <param name="rtpSession">The RTP session to use for sending and receiving.</param>
        /// <param name="microphone">The default system  audio input device found.</param>
        /// <param name="speaker">The default system audio output device.</param>
        public static void ConnectAudioDevicesToRtp(RTPMediaSession rtpSession, WaveInEvent microphone, BufferedWaveProvider speaker)
        {
            // Wire up the RTP send session to the audio input device.
            uint rtpSendTimestamp = 0;
            microphone.DataAvailable += (object sender, WaveInEventArgs args) =>
            {
                byte[] sample = new byte[args.Buffer.Length / 2];
                int sampleIndex = 0;

                for (int index = 0; index < args.BytesRecorded; index += 2)
                {
                    var ulawByte = NAudio.Codecs.MuLawEncoder.LinearToMuLawSample(BitConverter.ToInt16(args.Buffer, index));
                    sample[sampleIndex++] = ulawByte;
                }

                if (rtpSession.DestinationEndPoint != null)
                {
                    rtpSession.SendAudioFrame(rtpSendTimestamp, sample);
                    rtpSendTimestamp += (uint)(8000 / microphone.BufferMilliseconds);
                }
            };

            // Wire up the RTP receive session to the audio output device.
            rtpSession.OnRtpPacketReceived += (rtpPacket) =>
            {
                var sample = rtpPacket.Payload;
                for (int index = 0; index < sample.Length; index++)
                {
                    short pcm = NAudio.Codecs.MuLawDecoder.MuLawToLinearSample(sample[index]);
                    byte[] pcmSample = new byte[] { (byte)(pcm & 0xFF), (byte)(pcm >> 8) };
                    speaker.AddSamples(pcmSample, 0, 2);
                }
            };

           

        }


        private static async Task SendRtp(RTPSession rtpSession, IPEndPoint dstRtpEndPoint, string audioFileName, CancellationTokenSource cts)
        {
            try
            {
                string audioFileExt = Path.GetExtension(audioFileName).ToLower();

                switch (audioFileExt)
                {
                    case ".g722":
                    case ".ulaw":
                        {
                            uint timestamp = 0;
                            using (StreamReader sr = new StreamReader(audioFileName))
                            {
                                byte[] buffer = new byte[320];
                                int bytesRead = sr.BaseStream.Read(buffer, 0, buffer.Length);

                                while (bytesRead > 0 && !cts.IsCancellationRequested)
                                {
                                    if (!dstRtpEndPoint.Address.Equals(IPAddress.Any))
                                    {
                                        rtpSession.SendAudioFrame(timestamp, buffer);
                                    }

                                    timestamp += (uint)buffer.Length;

                                    await Task.Delay(40, cts.Token);
                                    bytesRead = sr.BaseStream.Read(buffer, 0, buffer.Length);
                                }
                            }
                        }
                        break;

                    case ".mp3":
                        {
                            var pcmFormat = new WaveFormat(8000, 16, 1);
                            var ulawFormat = WaveFormat.CreateMuLawFormat(8000, 1);

                            uint timestamp = 0;

                            using (WaveFormatConversionStream pcmStm = new WaveFormatConversionStream(pcmFormat, new Mp3FileReader(audioFileName)))
                            {
                                using (WaveFormatConversionStream ulawStm = new WaveFormatConversionStream(ulawFormat, pcmStm))
                                {
                                    byte[] buffer = new byte[320];
                                    int bytesRead = ulawStm.Read(buffer, 0, buffer.Length);

                                    while (bytesRead > 0 && !cts.IsCancellationRequested)
                                    {
                                        byte[] sample = new byte[bytesRead];
                                        Array.Copy(buffer, sample, bytesRead);

                                        if (dstRtpEndPoint.Address != IPAddress.Any)
                                        {
                                            rtpSession.SendAudioFrame(timestamp, buffer);
                                        }

                                        timestamp += (uint)buffer.Length;

                                        await Task.Delay(40, cts.Token);
                                        bytesRead = ulawStm.Read(buffer, 0, buffer.Length);
                                    }
                                }
                            }
                        }
                        break;

                    default:
                        throw new NotImplementedException($"The {audioFileExt} file type is not understood by this example.");
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception excp)
            {
                SIPSorcery.Sys.Log.Logger.LogError($"Exception sending RTP. {excp.Message}");
            }
        }
    }


}
