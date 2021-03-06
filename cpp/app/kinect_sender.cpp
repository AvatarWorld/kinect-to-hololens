#include <chrono>
#include <iostream>
#include <random>
#include "readerwriterqueue/readerwriterqueue.h"
#include "helper/kinect_helper.h"
#include "kh_sender.h"
#include "kh_trvl.h"
#include "kh_vp8.h"
#include "kh_packet_helper.h"

namespace kh
{
// Pair of the frame's id and its packets.
using steady_clock = std::chrono::steady_clock;
template<class T> using duration = std::chrono::duration<T>;
template<class T> using time_point = std::chrono::time_point<T>;
using FramePacketSet = std::pair<int, std::vector<std::vector<uint8_t>>>;
template<class T> using ReaderWriterQueue = moodycamel::ReaderWriterQueue<T>;

void run_sender_thread(int session_id,
                       bool& stop_sender_thread,
                       Sender& sender,
                       ReaderWriterQueue<FramePacketSet>& frame_packet_queue,
                       int& receiver_frame_id)
{
    const int XOR_MAX_GROUP_SIZE = 5;

    std::unordered_map<int, time_point<steady_clock>> frame_send_times;
    std::unordered_map<int, FramePacketSet> frame_packet_sets;
    int last_receiver_frame_id = 0;
    auto send_summary_start = steady_clock::now();
    int send_summary_receiver_frame_count = 0;
    int send_summary_receiver_packet_count = 0;
    int send_summary_packet_count = 0;
    while (!stop_sender_thread) {
        std::optional<std::vector<uint8_t>> received_packet;
        try {
            received_packet = sender.receive();
        } catch (std::system_error e) {
            printf("Error receving a packet: %s\n", e.what());
            goto run_sender_thread_end;
        }

        if (received_packet) {
            int cursor = 0;
            uint8_t message_type = copy_from_packet<uint8_t>(*received_packet, cursor);

            if (message_type == 1) {
                receiver_frame_id = copy_from_packet<int>(*received_packet, cursor);
                float packet_collection_time_ms = copy_from_packet<float>(*received_packet, cursor);
                float decoder_time_ms = copy_from_packet<float>(*received_packet, cursor);
                float frame_time_ms = copy_from_packet<float>(*received_packet, cursor);
                int receiver_packet_count = copy_from_packet<int>(*received_packet, cursor);

                duration<double> round_trip_time = steady_clock::now() - frame_send_times[receiver_frame_id];

                printf("Frame id: %d, packet: %f ms, decoder: %f ms, frame: %f ms, round_trip: %f ms\n",
                       receiver_frame_id, packet_collection_time_ms, decoder_time_ms, frame_time_ms,
                       round_trip_time.count() * 1000.0f);

                std::vector<int> obsolete_frame_ids;
                for (auto& frame_send_time_pair : frame_send_times) {
                    if (frame_send_time_pair.first <= receiver_frame_id)
                        obsolete_frame_ids.push_back(frame_send_time_pair.first);
                }

                for (int obsolete_frame_id : obsolete_frame_ids)
                    frame_send_times.erase(obsolete_frame_id);

                ++send_summary_receiver_frame_count;
                send_summary_receiver_packet_count += receiver_packet_count;
            } else if (message_type == 2) {
                int requested_frame_id = copy_from_packet<int>(*received_packet, cursor);
                int missing_packet_count = copy_from_packet<int>(*received_packet, cursor);
                
                for (int i = 0; i < missing_packet_count; ++i) {
                    int missing_packet_index = copy_from_packet<int>(*received_packet, cursor);

                    if (frame_packet_sets.find(requested_frame_id) == frame_packet_sets.end())
                        continue;

                    try {
                        sender.sendPacket(frame_packet_sets[requested_frame_id].second[missing_packet_index]);
                        ++send_summary_packet_count;
                    } catch (std::system_error e) {
                        if (e.code() == asio::error::would_block) {
                            printf("Failed to fill in a packet as the buffer was full...\n");
                        } else {
                            printf("Error while filling in a packet: %s\n", e.what());
                            goto run_sender_thread_end;
                        }
                    }
                }
            }
        }

        FramePacketSet frame_packet_set;
        while (frame_packet_queue.try_dequeue(frame_packet_set)) {
            auto xor_packets = Sender::createXorPackets(session_id, frame_packet_set.first, frame_packet_set.second, XOR_MAX_GROUP_SIZE);

            frame_send_times[frame_packet_set.first] = steady_clock::now();
            for (auto packet : frame_packet_set.second) {
                try {
                    sender.sendPacket(packet);
                    ++send_summary_packet_count;
                } catch (std::system_error e) {
                    if (e.code() == asio::error::would_block) {
                        printf("Failed to send a frame packet as the buffer was full...\n");
                    } else {
                        printf("Error from sending a frame packet: %s\n", e.what());
                        goto run_sender_thread_end;
                    }
                }
            }

            for (auto packet : xor_packets) {
                try {
                    sender.sendPacket(packet);
                    ++send_summary_packet_count;
                } catch (std::system_error e) {
                    if (e.code() == asio::error::would_block) {
                        printf("Failed to send an xor packet as the buffer was full...\n");
                    } else {
                        printf("Error from sending an xor packet: %s\n", e.what());
                        goto run_sender_thread_end;
                    }
                }
            }
            frame_packet_sets[frame_packet_set.first] = std::move(frame_packet_set);
        }

        // Remove elements of frame_packet_sets reserved for filling up missing packets
        // if they are already used from the receiver side.
        std::vector<int> obsolete_frame_ids;
        for (auto& frame_packet_set_pair : frame_packet_sets) {
            if (frame_packet_set_pair.first <= receiver_frame_id)
                obsolete_frame_ids.push_back(frame_packet_set_pair.first);
        }

        for (int obsolete_frame_id : obsolete_frame_ids)
            frame_packet_sets.erase(obsolete_frame_id);

        if ((receiver_frame_id / 100) > (last_receiver_frame_id / 100)) {
            duration<double> send_summary_time_interval = steady_clock::now() - send_summary_start;
            float packet_loss = 1.0f - send_summary_receiver_packet_count / (float)send_summary_packet_count;
            printf("Send Summary: Receiver FPS: %lf, Packet Loss: %f%%\n",
                   send_summary_receiver_frame_count / send_summary_time_interval.count(),
                   packet_loss * 100.0f);

            send_summary_start = steady_clock::now();
            send_summary_receiver_frame_count = 0;
            send_summary_packet_count = 0;
            send_summary_receiver_packet_count = 0;
        }
        last_receiver_frame_id = receiver_frame_id;
    }

run_sender_thread_end:
    stop_sender_thread = true;
    return;
}

void send_frames(int session_id, KinectDevice& device, int port)
{
    const int TARGET_BITRATE = 2000;
    const short CHANGE_THRESHOLD = 10;
    const int INVALID_THRESHOLD = 2;
    const int SENDER_SEND_BUFFER_SIZE = 1024 * 1024;
    //const int SENDER_SEND_BUFFER_SIZE = 128 * 1024;

    printf("Start Sending Frames (session_id: %d, port: %d)\n", session_id, port);

    auto calibration = device.getCalibration();
    k4a::transformation transformation(calibration);

    int depth_width = calibration.depth_camera_calibration.resolution_width;
    int depth_height = calibration.depth_camera_calibration.resolution_height;
    
    // Color encoder also uses the depth width/height since color pixels get transformed to the depth camera.
    Vp8Encoder color_encoder(depth_width, depth_height, TARGET_BITRATE);
    TrvlEncoder depth_encoder(depth_width * depth_height, CHANGE_THRESHOLD, INVALID_THRESHOLD);

    asio::io_context io_context;
    asio::ip::udp::socket socket(io_context, asio::ip::udp::endpoint(asio::ip::udp::v4(), port));

    std::vector<uint8_t> ping_buffer(1);
    asio::ip::udp::endpoint remote_endpoint;
    std::error_code error;
    socket.receive_from(asio::buffer(ping_buffer), remote_endpoint, 0, error);

    if (error) {
        printf("Error receiving ping: %s\n", error.message().c_str());
        throw std::system_error(error);
    }

    printf("Found a Receiver at %s:%d\n", remote_endpoint.address().to_string().c_str(), remote_endpoint.port());

    // Sender is a class that will use the socket to send frames to the receiver that has the socket connected to this socket.
    Sender sender(std::move(socket), remote_endpoint, SENDER_SEND_BUFFER_SIZE);
    sender.sendInitPacket(session_id, calibration);

    bool stop_sender_thread = false;
    moodycamel::ReaderWriterQueue<FramePacketSet> frame_packet_queue;
    // receiver_frame_id is the ID that the receiver sent back saying it received the frame of that ID.
    int receiver_frame_id = 0;
    std::thread sender_thread(run_sender_thread, session_id, std::ref(stop_sender_thread), std::ref(sender),
                              std::ref(frame_packet_queue), std::ref(receiver_frame_id));
    
    // frame_id is the ID of the frame the sender sends.
    int frame_id = 0;

    // Variables for profiling the sender.
    int main_summary_keyframe_count = 0;
    std::chrono::microseconds last_time_stamp;

    auto main_summary_start = steady_clock::now();
    size_t main_summary_frame_size_sum = 0;
    for (;;) {
        // Stop if the sender thread stopped.
        if (stop_sender_thread)
            break;

        auto capture = device.getCapture();
        if (!capture)
            continue;

        auto color_image = capture->get_color_image();
        if (!color_image) {
            printf("get_color_image() failed...\n");
            continue;
        }

        auto depth_image = capture->get_depth_image();
        if (!depth_image) {
            printf("get_depth_image() failed...\n");
            continue;
        }

        auto time_stamp = color_image.get_device_timestamp();
        auto time_diff = time_stamp - last_time_stamp;
        float frame_time_stamp = time_stamp.count() / 1000.0f;
        int frame_id_diff = frame_id - receiver_frame_id;
        int device_frame_diff = (int)(time_diff.count() / 33000.0f + 0.5f);
        if (device_frame_diff < static_cast<int>(std::pow(2, frame_id_diff - 3)))
            continue;
        
        last_time_stamp = time_stamp;

        bool keyframe = frame_id_diff > 5;

        auto transformed_color_image = transformation.color_image_to_depth_camera(depth_image, color_image);

        // Format the color pixels from the Kinect for the Vp8Encoder then encode the pixels with Vp8Encoder.
        auto yuv_image = createYuvImageFromAzureKinectBgraBuffer(transformed_color_image.get_buffer(),
                                                                 transformed_color_image.get_width_pixels(),
                                                                 transformed_color_image.get_height_pixels(),
                                                                 transformed_color_image.get_stride_bytes());
        auto vp8_frame = color_encoder.encode(yuv_image, keyframe);

        // Compress the depth pixels.
        auto depth_encoder_frame = depth_encoder.encode(reinterpret_cast<short*>(depth_image.get_buffer()), keyframe);

        auto message = Sender::createFrameMessage(frame_time_stamp, keyframe, vp8_frame,
                                                    reinterpret_cast<uint8_t*>(depth_encoder_frame.data()),
                                                    static_cast<uint32_t>(depth_encoder_frame.size()));
        auto packets = Sender::createFramePackets(session_id, frame_id, message);
        frame_packet_queue.enqueue(FramePacketSet(frame_id, std::move(packets)));


        // Updating variables for profiling.
        if (keyframe)
            ++main_summary_keyframe_count;
        main_summary_frame_size_sum += (vp8_frame.size() + depth_encoder_frame.size());

        // Print profile measures every 100 frames.
        if (frame_id % 100 == 0) {
            duration<double> main_summary_time_interval = steady_clock::now() - main_summary_start;
            printf("Main Summary id: %d, FPS: %lf, Keyframe Ratio: %d%%, Bandwidth: %lf Mbps\n",
                   frame_id,
                   100 / main_summary_time_interval.count(),
                   main_summary_keyframe_count,
                   main_summary_frame_size_sum / (main_summary_time_interval.count() * 131072));

            main_summary_start = steady_clock::now();
            main_summary_keyframe_count = 0;
            main_summary_frame_size_sum = 0;
        }
        ++frame_id;
    }
    stop_sender_thread = true;
    sender_thread.join();
}

// Repeats collecting the port number from the user and calling _send_frames() with it.
void main()
{
    srand(time(nullptr));
    std::mt19937 rng(steady_clock::now().time_since_epoch().count());

    for (;;) {
        std::string line;
        printf("Enter a port number to start sending frames: ");
        std::getline(std::cin, line);
        // The default port (the port when nothing is entered) is 7777.
        int port = line.empty() ? 7777 : std::stoi(line);

        k4a_device_configuration_t configuration = K4A_DEVICE_CONFIG_INIT_DISABLE_ALL;
        configuration.color_format = K4A_IMAGE_FORMAT_COLOR_BGRA32;
        configuration.color_resolution = K4A_COLOR_RESOLUTION_720P;
        configuration.depth_mode = K4A_DEPTH_MODE_NFOV_UNBINNED;
        auto timeout = std::chrono::milliseconds(1000);

        auto device = KinectDevice::create(configuration, timeout);
        if (!device) {
            printf("Failed to create a KinectDevice...\n");
            continue;
        }
        device->start();

        int session_id = rng() % (INT_MAX + 1);

        try {
            send_frames(session_id, *device, port);
        } catch (std::exception & e) {
            printf("Error from _send_frames: %s\n", e.what());
        }
    }
}
}

int main()
{
    kh::main();
    return 0;
}