using System;
using System.Threading;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorcery.Net;
using System.Net.Sockets;
using System.Net;
using NAudio.Wave;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;

namespace SIPClient1
{
    class Program
    {
        private static int SIP_LISTEN_PORT = 5060;
        private static readonly string DEFAULT_DESTINATION_SIP_URI = "sip:1772@10.84.0.250";
        private static readonly string TRANSFER_DESTINATION_SIP_URI = "sip:*1451@10.84.0.250";  // The destination to transfer the initial call to.
        private static int TRANSFER_TIMEOUT_SECONDS = 10;                    // Give up on transfer if no response within this period.
        private static Microsoft.Extensions.Logging.ILogger Log = SIPSorcery.Sys.Log.Logger;
        static void Main(string[] args)
        {
            Console.WriteLine("SIPSorcery call hold example.");
            Console.WriteLine("Press ctrl-c to exit.");

            // Plumbing code to facilitate a graceful exit.
            CancellationTokenSource exitCts = new CancellationTokenSource(); // Cancellation token to stop the SIP transport and RTP stream.
            bool isCallHungup = false;
            bool hasCallFailed = false;

            Utils.AddConsoleLogger();

            // Set up a default SIP transport.
            var sipTransport = new SIPTransport();
            sipTransport.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(IPAddress.Any, SIP_LISTEN_PORT)));

            //EnableTraceLogs(sipTransport);

            // Get the default speaker.
            var (audioOutEvent, audioOutProvider) = Utils.GetAudioOutputDevice();
            WaveInEvent waveInEvent = Utils.GetAudioInputDevice();



            RTPMediaSession RtpMediaSession = null;

            // Create a client user agent to maintain a periodic registration with a SIP server.
            var regUserAgent = new SIPRegistrationUserAgent(
                sipTransport,
                "1772",
                "aaa10800bd32115d86e548b8dfb21816",
                "10.84.0.250",
                120);

            ManualResetEvent taskCompleteMre = new ManualResetEvent(false);

            Console.CancelKeyPress += delegate (object sender, ConsoleCancelEventArgs e)
            {
                e.Cancel = true;
                SIPSorcery.Sys.Log.Logger.LogInformation("Exiting...");
                taskCompleteMre.Set();
            };

            // Start the thread to perform the initial registration and then periodically resend it.
            regUserAgent.Start();
            

            // Create a client/server user agent to place a call to a remote SIP server along with event handlers for the different stages of the call.
            var userAgent = new SIPUserAgent(sipTransport, null);

            userAgent.ClientCallTrying += (uac, resp) => Log.LogInformation($"{uac.CallDescriptor.To} Trying: {resp.StatusCode} {resp.ReasonPhrase}.");
            userAgent.ClientCallRinging += (uac, resp) => Log.LogInformation($"{uac.CallDescriptor.To} Ringing: {resp.StatusCode} {resp.ReasonPhrase}.");
            userAgent.ClientCallFailed += (uac, err) =>
            {
                Log.LogWarning($"{uac.CallDescriptor.To} Failed: {err}");
                hasCallFailed = true;
                exitCts.Cancel();
            };
            userAgent.ClientCallAnswered += (uac, resp) =>
            {
                if (resp.Status == SIPResponseStatusCodesEnum.Ok)
                {
                    Log.LogInformation($"{uac.CallDescriptor.To} Answered: {resp.StatusCode} {resp.ReasonPhrase}.");
                    Utils.PlayRemoteMedia(RtpMediaSession, audioOutProvider);
                }
                else
                {
                    Log.LogWarning($"{uac.CallDescriptor.To} Answered: {resp.StatusCode} {resp.ReasonPhrase}.");
                    hasCallFailed = true;
                    exitCts.Cancel();
                }
            };
            userAgent.OnCallHungup += () =>
            {
                Log.LogInformation($"Call hungup by remote party.");
                exitCts.Cancel();
            };
            userAgent.ServerCallCancelled += (uas) => Log.LogInformation("Incoming call cancelled by caller.");

            sipTransport.SIPTransportRequestReceived += async (localEndPoint, remoteEndPoint, sipRequest) =>
            {
                if (sipRequest.Header.From != null &&
                    sipRequest.Header.From.FromTag != null &&
                    sipRequest.Header.To != null &&
                    sipRequest.Header.To.ToTag != null)
                {
                    // This is an in-dialog request that will be handled directly by a user agent instance.
                }
                else if (sipRequest.Method == SIPMethodsEnum.INVITE)
                {
                    if (userAgent?.IsCallActive == true)
                    {
                        Log.LogWarning($"Busy response returned for incoming call request from {remoteEndPoint}: {sipRequest.StatusLine}.");
                        // If we are already on a call return a busy response.
                        UASInviteTransaction uasTransaction = new UASInviteTransaction(sipTransport, sipRequest, null);
                        SIPResponse busyResponse = SIPResponse.GetResponse(sipRequest, SIPResponseStatusCodesEnum.BusyHere, null);
                        uasTransaction.SendFinalResponse(busyResponse);
                    }
                    else
                    {
                        Log.LogInformation($"Incoming call request from {remoteEndPoint}: {sipRequest.StatusLine}.");
                        var incomingCall = userAgent.AcceptCall(sipRequest);

                        RtpMediaSession = new RTPMediaSession(SDPMediaTypesEnum.audio, (int)SDPMediaFormatsEnum.PCMU, AddressFamily.InterNetwork);
                        RtpMediaSession.RemotePutOnHold += () => Log.LogInformation("Remote call party has placed us on hold.");
                        RtpMediaSession.RemoteTookOffHold += () => Log.LogInformation("Remote call party took us off hold.");
                        await userAgent.Answer(incomingCall, RtpMediaSession);

                        Utils.PlayRemoteMedia(RtpMediaSession, audioOutProvider);
                        waveInEvent.StartRecording();

                        Log.LogInformation($"Answered incoming call from {sipRequest.Header.From.FriendlyDescription()} at {remoteEndPoint}.");
                    }
                }
                else
                {
                    Log.LogDebug($"SIP {sipRequest.Method} request received but no processing has been set up for it, rejecting.");
                    SIPResponse notAllowedResponse = SIPResponse.GetResponse(sipRequest, SIPResponseStatusCodesEnum.MethodNotAllowed, null);
                    await sipTransport.SendResponseAsync(notAllowedResponse);
                }
            };


            // Wire up the RTP send session to the audio output device.
            uint rtpSendTimestamp = 0;
            waveInEvent.DataAvailable += (object sender, WaveInEventArgs args) =>
            {
                byte[] sample = new byte[args.Buffer.Length / 2];
                int sampleIndex = 0;

                for (int index = 0; index < args.BytesRecorded; index += 2)
                {
                    var ulawByte = NAudio.Codecs.MuLawEncoder.LinearToMuLawSample(BitConverter.ToInt16(args.Buffer, index));
                    sample[sampleIndex++] = ulawByte;
                }

                if (RtpMediaSession != null)
                {
                    RtpMediaSession.SendAudioFrame(rtpSendTimestamp, sample);
                    rtpSendTimestamp += (uint)(8000 / waveInEvent.BufferMilliseconds);
                }
            };

            // At this point the call has been initiated and everything will be handled in an event handler.
            Task.Run(async () =>
            {
                try
                {
                    while (!exitCts.Token.WaitHandle.WaitOne(0))
                    {
                        var keyProps = Console.ReadKey();

                        if (keyProps.KeyChar == 'c')
                        {
                            if (!userAgent.IsCallActive)
                            {
                                RtpMediaSession = new RTPMediaSession(SDPMediaTypesEnum.audio, (int)SDPMediaFormatsEnum.PCMU, AddressFamily.InterNetwork);
                                RtpMediaSession.RemotePutOnHold += () => Log.LogInformation("Remote call party has placed us on hold.");
                                RtpMediaSession.RemoteTookOffHold += () => Log.LogInformation("Remote call party took us off hold.");

                                var callDescriptor = Utils.GetCallDescriptor(DEFAULT_DESTINATION_SIP_URI);
                                await userAgent.InitiateCall(callDescriptor, RtpMediaSession);
                            }
                            else
                            {
                                Log.LogWarning("There is already an active call.");
                            }
                        }
                        else if (keyProps.KeyChar == 'h')
                        {
                            // Place call on/off hold.
                            if (userAgent.IsCallActive)
                            {
                                if (RtpMediaSession.LocalOnHold)
                                {
                                    Log.LogInformation("Taking the remote call party off hold.");
                                    RtpMediaSession.TakeOffHold();
                                }
                                else
                                {
                                    Log.LogInformation("Placing the remote call party on hold.");
                                    RtpMediaSession.PutOnHold();
                                }
                            }
                            else
                            {
                                Log.LogWarning("There is no active call to put on hold.");
                            }
                        }
                        else if (keyProps.KeyChar == 't')
                        {
                            if (userAgent.IsCallActive)
                            {
                                var transferURI = SIPURI.ParseSIPURI(TRANSFER_DESTINATION_SIP_URI);
                                bool result = await userAgent.BlindTransfer(transferURI, TimeSpan.FromSeconds(TRANSFER_TIMEOUT_SECONDS), exitCts.Token);
                                if (result)
                                {
                                    // If the transfer was accepted the original call will already have been hungup.
                                    // Wait a second for the transfer NOTIFY request to arrive.
                                    await Task.Delay(1000);
                                    exitCts.Cancel();
                                }
                                else
                                {
                                    Log.LogWarning($"Transfer to {TRANSFER_DESTINATION_SIP_URI} failed.");
                                }
                            }
                            else
                            {
                                Log.LogWarning("There is no active call to transfer.");
                            }
                        }
                        else if (keyProps.KeyChar == 'q')
                        {
                            // Quit application.
                            exitCts.Cancel();
                        }
                    }
                }
                catch (Exception excp)
                {
                    SIPSorcery.Sys.Log.Logger.LogError($"Exception Key Press listener. {excp.Message}.");
                }
            });

            // Wait for a signal saying the call failed, was cancelled with ctrl-c or completed.
            exitCts.Token.WaitHandle.WaitOne();

            // Ctrl-c will gracefully exit the call at any point.
            Console.CancelKeyPress += delegate (object sender, ConsoleCancelEventArgs e)
            {
                e.Cancel = true;
                exitCts.Cancel();
            };

            taskCompleteMre.WaitOne();

            regUserAgent.Stop();
            if (sipTransport != null)
            {
                SIPSorcery.Sys.Log.Logger.LogInformation("Shutting down SIP transport...");
                sipTransport.Shutdown();
            }
            SIPSorcery.Net.DNSManager.Stop();
        }
    }
}
