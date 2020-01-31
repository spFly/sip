﻿//-----------------------------------------------------------------------------
// Filename: STUNClient.cs
//
// Description: 
//
// Author(s):
// Aaron Clauson
//
// History:
// ??	Aaron Clauson	Created (aaron@sipsorcery.com), SIP Sorcery PTY LTD, Hobart, Australia (www.sipsorcery.com).
//
// License: 
// BSD 3-Clause "New" or "Revised" License, see included LICENSE.md file.
//-----------------------------------------------------------------------------

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Microsoft.Extensions.Logging;
using SIPSorcery.Sys;

namespace SIPSorcery.Net
{
    public class STUNClient
    {
        public const int DEFAULT_STUN_PORT = 3478;
        private const int STUN_SERVER_RESPONSE_TIMEOUT = 3;

        private static ILogger logger = Log.Logger;

        public static IPAddress GetPublicIPAddress(string stunServer, int port = DEFAULT_STUN_PORT)
        {
            try
            {
                logger.LogDebug("STUNClient attempting to determine public IP from " + stunServer + ".");

                using (UdpClient udpClient = new UdpClient(stunServer, port))
                {
                    STUNMessage initMessage = new STUNMessage(STUNMessageTypesEnum.BindingRequest);
                    byte[] stunMessageBytes = initMessage.ToByteBuffer();
                    udpClient.Send(stunMessageBytes, stunMessageBytes.Length);

                    IPAddress publicIPAddress = null;
                    ManualResetEvent gotResponseMRE = new ManualResetEvent(false);

                    udpClient.BeginReceive((ar) =>
                    {
                        try
                        {
                            IPEndPoint stunResponseEndPoint = null;
                            byte[] stunResponseBuffer = udpClient.EndReceive(ar, ref stunResponseEndPoint);

                            if (stunResponseBuffer != null && stunResponseBuffer.Length > 0)
                            {
                                logger.LogDebug("STUNClient Response to initial STUN message received from " + stunResponseEndPoint + ".");
                                STUNMessage stunResponse = STUNMessage.ParseSTUNMessage(stunResponseBuffer, stunResponseBuffer.Length);

                                if (stunResponse.Attributes.Count > 0)
                                {
                                    foreach (STUNAttribute stunAttribute in stunResponse.Attributes)
                                    {
                                        if (stunAttribute.AttributeType == STUNAttributeTypesEnum.MappedAddress)
                                        {
                                            publicIPAddress = ((STUNAddressAttribute)stunAttribute).Address;
                                            logger.LogDebug("STUNClient Public IP=" + publicIPAddress.ToString() + ".");
                                        }
                                    }
                                }
                            }

                            gotResponseMRE.Set();
                        }
                        catch (Exception recvExcp)
                        {
                            logger.LogWarning("Exception STUNClient Receive. " + recvExcp.Message);
                        }
                    }, null);

                    if (gotResponseMRE.WaitOne(STUN_SERVER_RESPONSE_TIMEOUT * 1000))
                    {
                        return publicIPAddress;
                    }
                    else
                    {
                        logger.LogWarning("STUNClient server response timedout after " + STUN_SERVER_RESPONSE_TIMEOUT + "s.");
                        return null;
                    }
                }
            }
            catch (Exception excp)
            {
                logger.LogError("Exception STUNClient GetPublicIPAddress. " + excp.Message);
                return null;
                //throw;
            }
        }
    }
}
