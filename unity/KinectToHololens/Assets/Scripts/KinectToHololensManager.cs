﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.XR.WSA.Input;

class XorPacketCollection
{
    public int FrameId { get; private set; }
    public int PacketCount { get; private set; }
    private byte[][] packets;
    public XorPacketCollection(int frameId, int packetCount)
    {
        FrameId = frameId;
        PacketCount = packetCount;
        packets = new byte[packetCount][];
        for (int i = 0; i < packetCount; ++i)
        {
            packets[i] = new byte[0];
        }
    }

    public void AddPacket(int packetIndex, byte[] packet)
    {
        packets[packetIndex] = packet;
    }

    public byte[] TryGetPacket(int packetIndex)
    {
        if(packets[packetIndex].Length == 0)
        {
            return null;
        }

        return packets[packetIndex];
    }
};

public class KinectToHololensManager : MonoBehaviour
{
    private enum InputState
    {
        IpAddress, Port
    }

    // The main camera's Transform.
    public Transform cameraTransform;
    // The TextMesh placed above user's head.
    public TextMesh statusText;
    // The root of the scene that includes everything else except the main camera.
    // This provides a convenient way to place everything in front of the camera.
    public Transform scenceRootTransform;
    // TextMeshes for the UI.
    public TextMesh ipAddressText;
    public TextMesh ipAddressInputField;
    public TextMesh portText;
    public TextMesh portInputField;
    public TextMesh instructionText;
    // For rendering the Kinect pixels in 3D.
    public Material azureKinectScreenMaterial;
    public AzureKinectScreen azureKinectScreen;

    // To recognize when the user taps.
    private GestureRecognizer gestureRecognizer;
    // Varaibles that represent states of the scene.
    private InputState inputState;
    private TextureGroup textureGroup;
    private Receiver receiver;
    private Vp8Decoder colorDecoder;
    private TrvlDecoder depthDecoder;

    //private Dictionary<int, FramePacketCollection> framePacketCollections;
    private List<FrameMessage> frameMessages;
    private int lastFrameId;
    private Stopwatch frameStopWatch;
    //private int? serverSessionId;
    private int summaryPacketCount;

    private bool stopReceiverThread;
    private ConcurrentQueue<byte[]> initPacketQueue;
    private ConcurrentQueue<FrameMessage> frameMessageQueue;

    public TextMesh ActiveInputField
    {
        get
        {
            return inputState == InputState.IpAddress ? ipAddressInputField : portInputField;
        }
    }

    public bool UiVisibility
    {
        set
        {
            ipAddressText.gameObject.SetActive(value);
            ipAddressInputField.gameObject.SetActive(value);
            portText.gameObject.SetActive(value);
            portInputField.gameObject.SetActive(value);
            instructionText.gameObject.SetActive(value);
        }
        get
        {
            return ipAddressText.gameObject.activeSelf;
        }
    }

    void Awake()
    {
        gestureRecognizer = new GestureRecognizer();
        receiver = null;
        textureGroup = null;
        UiVisibility = true;
        SetInputState(InputState.IpAddress);

        frameMessages = new List<FrameMessage>();
        lastFrameId = -1;
        frameStopWatch = Stopwatch.StartNew();
        //serverSessionId = null;
        summaryPacketCount = 0;

        stopReceiverThread = false;
        initPacketQueue = new ConcurrentQueue<byte[]>();
        frameMessageQueue = new ConcurrentQueue<FrameMessage>();

        // Prepare a GestureRecognizer to recognize taps.
        gestureRecognizer.Tapped += OnTapped;
        gestureRecognizer.StartCapturingGestures();

        statusText.text = "Waiting for user input.";

        Plugin.texture_group_reset();
    }

    void Update()
    {
        // Space key resets the scene to be placed in front of the camera.
        if (Input.GetKeyDown(KeyCode.Space))
        {
            ResetView();
        }

        // Sends virtual keyboards strokes to the TextMeshes for the IP address and the port.
        AbsorbInput();

        // If texture is not created, create and assign them to quads.
        if (textureGroup == null)
        {
            // Check whether the native plugin has Direct3D textures that
            // can be connected to Unity textures.
            if (Plugin.texture_group_get_y_texture_view().ToInt64() != 0)
            {
                // TextureGroup includes Y, U, V, and a depth texture.
                textureGroup = new TextureGroup(Plugin.texture_group_get_width(),
                                                Plugin.texture_group_get_height());

                azureKinectScreenMaterial.SetTexture("_YTex", textureGroup.YTexture);
                azureKinectScreenMaterial.SetTexture("_UTex", textureGroup.UTexture);
                azureKinectScreenMaterial.SetTexture("_VTex", textureGroup.VTexture);
                azureKinectScreenMaterial.SetTexture("_DepthTex", textureGroup.DepthTexture);

                print("textureGroup intialized");
            }
        }

        if (receiver == null)
            return;

        while (initPacketQueue.TryDequeue(out byte[] packet))
        {
            int cursor = 0;
            //int sessionId = BitConverter.ToInt32(packet, cursor);
            cursor += 4;

            //var packetType = packet[cursor];
            cursor += 1;

            var calibration = ManagerHelper.ReadAzureKinectCalibrationFromMessage(packet, cursor);

            Plugin.texture_group_set_width(calibration.DepthCamera.Width);
            Plugin.texture_group_set_height(calibration.DepthCamera.Height);
            PluginHelper.InitTextureGroup();

            colorDecoder = new Vp8Decoder();
            depthDecoder = new TrvlDecoder(calibration.DepthCamera.Width * calibration.DepthCamera.Height);

            azureKinectScreen.Setup(calibration);
        }

        while (frameMessageQueue.TryDequeue(out FrameMessage frameMessage))
        {
            frameMessages.Add(frameMessage);
        }

        frameMessages.Sort((x, y) => x.FrameId.CompareTo(y.FrameId));
        
        if (frameMessages.Count == 0)
        {
            return;
        }

        int? beginIndex = null;
        // If there is a key frame, use the most recent one.
        for (int i = frameMessages.Count - 1; i >= 0; --i)
        {
            if (frameMessages[i].Keyframe)
            {
                beginIndex = i;
                break;
            }
        }

        // When there is no key frame, go through all the frames if the first
        // FrameMessage is the one right after the previously rendered one.
        if (!beginIndex.HasValue)
        {
            if (frameMessages[0].FrameId == lastFrameId + 1)
            {
                beginIndex = 0;
            }
            else
            {
                // Wait for more frames if there is way to render without glitches.
                return;
            }
        }

        // ffmpegFrame and trvlFrame are guaranteed to be non-null
        // since the existence of beginIndex's value.
        FFmpegFrame ffmpegFrame = null;
        TrvlFrame trvlFrame = null;
        TimeSpan packetCollectionTime;

        var decoderStopWatch = Stopwatch.StartNew();
        for (int i = beginIndex.Value; i < frameMessages.Count; ++i)
        {
            var frameMessage = frameMessages[i];
            lastFrameId = frameMessage.FrameId;

            packetCollectionTime = frameMessage.PacketCollectionTime;

            var colorEncoderFrame = frameMessage.GetColorEncoderFrame();
            var depthEncoderFrame = frameMessage.GetDepthEncoderFrame();

            IntPtr colorEncoderFrameBytes = Marshal.AllocHGlobal(colorEncoderFrame.Length);
            Marshal.Copy(colorEncoderFrame, 0, colorEncoderFrameBytes, colorEncoderFrame.Length);
            ffmpegFrame = colorDecoder.Decode(colorEncoderFrameBytes, colorEncoderFrame.Length);
            Marshal.FreeHGlobal(colorEncoderFrameBytes);


            IntPtr depthEncoderFrameBytes = Marshal.AllocHGlobal(depthEncoderFrame.Length);
            Marshal.Copy(depthEncoderFrame, 0, depthEncoderFrameBytes, depthEncoderFrame.Length);
            trvlFrame = depthDecoder.Decode(depthEncoderFrameBytes, frameMessage.Keyframe);
            Marshal.FreeHGlobal(depthEncoderFrameBytes);

        }
        decoderStopWatch.Stop();
        var decoderTime = decoderStopWatch.Elapsed;
        frameStopWatch.Stop();
        var frameTime = frameStopWatch.Elapsed;
        frameStopWatch = Stopwatch.StartNew();

        print($"id: {lastFrameId}, packet collection time: {packetCollectionTime.TotalMilliseconds}," +
              $"decoder time: {decoderTime.TotalMilliseconds}, frame time: {frameTime.TotalMilliseconds}");

        receiver.Send(lastFrameId, (float) packetCollectionTime.TotalMilliseconds, (float) decoderTime.TotalMilliseconds,
            (float) frameTime.TotalMilliseconds, summaryPacketCount);
        summaryPacketCount = 0;

        // Invokes a function to be called in a render thread.
        if (textureGroup != null)
        {
            Plugin.texture_group_set_ffmpeg_frame(ffmpegFrame.Ptr);
            Plugin.texture_group_set_depth_pixels(trvlFrame.Ptr);
            PluginHelper.UpdateTextureGroup();
        }

        frameMessages = new List<FrameMessage>();
    }

    void OnDestroy()
    {
        stopReceiverThread = true;
    }

    private void SetInputState(InputState inputState)
    {
        if (inputState == InputState.IpAddress)
        {
            ipAddressText.color = Color.yellow;
            portText.color = Color.white;
        }
        else
        {
            ipAddressText.color = Color.white;
            portText.color = Color.yellow;
        }

        this.inputState = inputState;
    }

    private void OnTapped(TappedEventArgs args)
    {
        // Place the scene in front of the camera when the user taps.
        ResetView();
    }

    // Places everything in front of the camera by positing and turning a root transform for
    // everything else except the camera.
    private void ResetView()
    {
        scenceRootTransform.localPosition = cameraTransform.localPosition;
        scenceRootTransform.localRotation = cameraTransform.localRotation;
    }

    // Sends keystrokes of the virtual keyboard to TextMeshes.
    // Try connecting the Receiver to a Sender when the user pressed the enter key.
    private void AbsorbInput()
    {
        if (Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.DownArrow) || Input.GetKeyDown(KeyCode.Tab))
        {
            SetInputState(inputState != InputState.IpAddress ? InputState.IpAddress : InputState.Port);
        }
        AbsorbKeyCode(KeyCode.Alpha0, '0');
        AbsorbKeyCode(KeyCode.Keypad0, '0');
        AbsorbKeyCode(KeyCode.Alpha1, '1');
        AbsorbKeyCode(KeyCode.Keypad1, '1');
        AbsorbKeyCode(KeyCode.Alpha2, '2');
        AbsorbKeyCode(KeyCode.Keypad2, '2');
        AbsorbKeyCode(KeyCode.Alpha3, '3');
        AbsorbKeyCode(KeyCode.Keypad3, '3');
        AbsorbKeyCode(KeyCode.Alpha4, '4');
        AbsorbKeyCode(KeyCode.Keypad4, '4');
        AbsorbKeyCode(KeyCode.Alpha5, '5');
        AbsorbKeyCode(KeyCode.Keypad5, '5');
        AbsorbKeyCode(KeyCode.Alpha6, '6');
        AbsorbKeyCode(KeyCode.Keypad6, '6');
        AbsorbKeyCode(KeyCode.Alpha7, '7');
        AbsorbKeyCode(KeyCode.Keypad7, '7');
        AbsorbKeyCode(KeyCode.Alpha8, '8');
        AbsorbKeyCode(KeyCode.Keypad8, '8');
        AbsorbKeyCode(KeyCode.Alpha9, '9');
        AbsorbKeyCode(KeyCode.Keypad9, '9');
        if (inputState == InputState.IpAddress)
        {
            AbsorbKeyCode(KeyCode.Period, '.');
            AbsorbKeyCode(KeyCode.KeypadPeriod, '.');
        }
        if (Input.GetKeyDown(KeyCode.Backspace))
        {
            var text = ActiveInputField.text;
            if (text.Length > 0)
            {
                ActiveInputField.text = text.Substring(0, text.Length - 1);
            }
        }
        if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) || Input.GetKeyDown("enter"))
        {
            Ping();
        }
    }

    // A helper method for AbsorbInput().
    private void AbsorbKeyCode(KeyCode keyCode, char c)
    {
        if (Input.GetKeyDown(keyCode))
        {
            ActiveInputField.text += c;
        }
    }

    // To copy the c++ receiver, for easier development,
    // there should be only one chance to send a ping.
    private void Ping()
    {
        if(!UiVisibility)
        {
            print("No more than one ping at a time.");
            return;
        }

        UiVisibility = false;

        // The default IP address is 127.0.0.1.
        string ipAddressText = ipAddressInputField.text;
        if (ipAddressText.Length == 0)
            ipAddressText = "127.0.0.1";

        // The default port is 7777.
        string portString = portInputField.text;
        int port = portString.Length != 0 ? int.Parse(portString) : 7777;

        string logString = $"Try connecting to {ipAddressText}:{port}...";
        print(logString);
        statusText.text = logString;

        var ipAddress = IPAddress.Parse(ipAddressText);
        receiver = new Receiver(1024 * 1024);
        receiver.Ping(ipAddress, port);

        Thread receiverThread = new Thread(RunReceiverThread);
        receiverThread.Start();
    }

    private void RunReceiverThread()
    {
        const int XOR_MAX_GROUP_SIZE = 5;

        int? senderSessionId = null;
        var framePacketCollections = new Dictionary<int, FramePacketCollection>();
        var xorPacketCollections = new Dictionary<int, XorPacketCollection>();
        print("Start Receiver Thread");
        while (!stopReceiverThread)
        {
            var framePackets = new List<byte[]>();
            var xorPackets = new List<byte[]>();
            while (true)
            {
                var packet = receiver.Receive();
                if (packet == null)
                    break;

                ++summaryPacketCount;

                int sessionId = BitConverter.ToInt32(packet, 0);
                var packetType = packet[4];

                if (packetType == 0)
                {
                    senderSessionId = sessionId;
                    initPacketQueue.Enqueue(packet);
                }

                if (!senderSessionId.HasValue || sessionId != senderSessionId.Value)
                    continue;

                if (packetType == 1)
                {
                    framePackets.Add(packet);
                }
                else if (packetType == 2)
                {
                    xorPackets.Add(packet);
                }
            }

            // The logic for XOR FEC packets are almost the same to frame packets.
            // The operations for XOR FEC packets should happen before the frame packets
            // so that frame packet can be created with XOR FEC packets when a missing
            // frame packet is detected.
            foreach (var xorPacket in xorPackets)
            {
                int cursor = 5;

                int frameId = BitConverter.ToInt32(xorPacket, cursor);
                cursor += 4;

                if (frameId <= lastFrameId)
                    continue;

                int packetIndex = BitConverter.ToInt32(xorPacket, cursor);
                cursor += 4;

                int packetCount = BitConverter.ToInt32(xorPacket, cursor);
                //cursor += 4;

                if (!xorPacketCollections.ContainsKey(frameId))
                {
                    xorPacketCollections[frameId] = new XorPacketCollection(frameId, packetCount);
                }

                xorPacketCollections[frameId].AddPacket(packetIndex, xorPacket);
            }

            foreach (var framePacket in framePackets)
            {
                // Rewrote with out using cursor since it causes a conflict with another cursor
                // below used for frame packet creation in foward error correction.
                //int cursor = 5;

                //int frameId = BitConverter.ToInt32(framePacket, cursor);
                //cursor += 4;

                //if (frameId <= lastFrameId)
                //    continue;

                //int packetIndex = BitConverter.ToInt32(framePacket, cursor);
                //cursor += 4;

                //int packetCount = BitConverter.ToInt32(framePacket, cursor);
                //cursor += 4;

                int frameId = BitConverter.ToInt32(framePacket, 5);

                if (frameId <= lastFrameId)
                    continue;

                int packetIndex = BitConverter.ToInt32(framePacket, 9);
                int packetCount = BitConverter.ToInt32(framePacket, 13);

                if (!framePacketCollections.ContainsKey(frameId))
                {
                    framePacketCollections[frameId] = new FramePacketCollection(frameId, packetCount);

                    ///////////////////////////////////
                    // Forward Error Correction Start//
                    ///////////////////////////////////
                    // Request missing packets of the previous frames.
                    foreach (var collectionPair in framePacketCollections)
                    {
                        if(collectionPair.Key < frameId)
                        {
                            int missingFrameId = collectionPair.Key;
                            var missingPacketIndices = collectionPair.Value.GetMissingPacketIds();

                            // Try correction using XOR FEC packets.
                            var fecFailedPacketIndices = new List<int>();

                            // missing_packet_index cannot get error corrected if there is another missing_packet_index
                            // that belongs to the same XOR FEC packet...
                            foreach (int i in missingPacketIndices)
                            {
                                foreach(int j in missingPacketIndices)
                                {
                                    if (i == j)
                                        continue;

                                    if((i / XOR_MAX_GROUP_SIZE) == (j / XOR_MAX_GROUP_SIZE))
                                        fecFailedPacketIndices.Add(i);
                                }
                            }

                            foreach (int missingPacketIndex in missingPacketIndices)
                            {
                                // If fec_failed_packet_indices already contains missing_packet_index, skip.
                                if (fecFailedPacketIndices.Contains(missingPacketIndex))
                                {
                                    continue;
                                }

                                // Try getting the XOR FEC packet for correction.
                                int xorPacketIndex = missingPacketIndex / XOR_MAX_GROUP_SIZE;
                                var xorPacket = xorPacketCollections[missingFrameId].TryGetPacket(xorPacketIndex);
                                // Give up if there is no xor packet yet.
                                if (xorPacket == null)
                                {
                                    fecFailedPacketIndices.Add(missingPacketIndex);
                                    continue;
                                }

                                const int PACKET_SIZE = 1500;
                                const int PACKET_HEADER_SIZE = 17;
                                const int MAX_PACKET_CONTENT_SIZE = PACKET_SIZE - PACKET_HEADER_SIZE;
                                byte[] fecFramePacket = new byte[PACKET_SIZE];

                                byte packetType = 1;
                                int cursor = 0;
                                Buffer.BlockCopy(BitConverter.GetBytes(senderSessionId.Value), 0, fecFramePacket, cursor, 4);
                                cursor += 4;

                                fecFramePacket[cursor] = packetType;
                                cursor += 1;

                                Buffer.BlockCopy(BitConverter.GetBytes(missingFrameId), 0, fecFramePacket, cursor, 4);
                                cursor += 4;

                                Buffer.BlockCopy(BitConverter.GetBytes(packetIndex), 0, fecFramePacket, cursor, 4);
                                cursor += 4;

                                Buffer.BlockCopy(BitConverter.GetBytes(packetCount), 0, fecFramePacket, cursor, 4);
                                cursor += 4;

                                Buffer.BlockCopy(xorPacket, cursor, fecFramePacket, cursor, MAX_PACKET_CONTENT_SIZE);

                                int beginFramePacketIndex = xorPacketIndex * XOR_MAX_GROUP_SIZE;
                                int endFramePacketIndex = Math.Min(beginFramePacketIndex + XOR_MAX_GROUP_SIZE, collectionPair.Value.PacketCount);

                                // Run bitwise XOR with all other packets belonging to the same XOR FEC packet.
                                for (int i = beginFramePacketIndex; i < endFramePacketIndex; ++i)
                                {
                                    if (i == missingPacketIndex)
                                        continue;

                                    for (int j = PACKET_HEADER_SIZE; j < PACKET_SIZE; ++j)
                                        fecFramePacket[j] ^= collectionPair.Value.Packets[i][j];
                                }

                                //print($"restored {missingFrameId} {missingPacketIndex}");
                                framePacketCollections[missingFrameId].AddPacket(missingPacketIndex, fecFramePacket);
                            } // end of foreach (int missingPacketIndex in missingPacketIndices)

                            //foreach (int fecFailedPacketIndex in fecFailedPacketIndices)
                            //{
                            //    print($"request {missingFrameId} {fecFailedPacketIndex}");
                            //}
                            receiver.Send(collectionPair.Key, fecFailedPacketIndices);
                        }
                    } // Forward Error Correction End
                }

                framePacketCollections[frameId].AddPacket(packetIndex, framePacket);
            }

            // Find all full collections and their frame_ids.
            var fullFrameIds = new List<int>();
            foreach (var collectionPair in framePacketCollections)
            {
                if (collectionPair.Value.IsFull())
                {
                    int frameId = collectionPair.Key;
                    fullFrameIds.Add(frameId);
                }
            }

            // Extract messages from the full collections.
            foreach (int fullFrameId in fullFrameIds)
            {
                frameMessageQueue.Enqueue(framePacketCollections[fullFrameId].ToMessage());
                framePacketCollections.Remove(fullFrameId);
            }

            // Clean up frame_packet_collections.
            var obsoleteFrameIds = new List<int>();
            foreach (var collectionPair in framePacketCollections)
            {
                if (collectionPair.Key <= lastFrameId)
                {
                    obsoleteFrameIds.Add(collectionPair.Key);
                }
            }

            foreach (int obsoleteFrameId in obsoleteFrameIds)
            {
                framePacketCollections.Remove(obsoleteFrameId);
            }
        }
        print("Receiver Thread Dead");
    }
}
