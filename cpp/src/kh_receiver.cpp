#include "kh_receiver.h"

#include <iostream>

namespace kh
{
Receiver::Receiver(asio::io_context& io_context, int receive_buffer_size)
    : socket_(io_context), remote_endpoint_()
{
    socket_.open(asio::ip::udp::v4());
    socket_.non_blocking(true);
    asio::socket_base::receive_buffer_size option(receive_buffer_size);
    socket_.set_option(option);
}

// Connects to a Sender with the Sender's IP address and port.
void Receiver::ping(std::string ip_address, int port)
{
    remote_endpoint_ = asio::ip::udp::endpoint(asio::ip::address::from_string(ip_address), port);
    std::array<char, 1> send_buf = { { 0 } };
    socket_.send_to(asio::buffer(send_buf), remote_endpoint_);
}

std::optional<std::vector<uint8_t>> Receiver::receive()
{
    std::vector<uint8_t> packet(1500);
    std::error_code error;
    size_t packet_size = socket_.receive_from(asio::buffer(packet), remote_endpoint_, 0, error);

    if (error == asio::error::would_block) {
        return std::nullopt;
    } else if (error) {
        std::cout << "Error from ReceiverUdp: " << error.message() << std::endl;
        return std::nullopt;
    }

    packet.resize(packet_size);
    return packet;
}

void Receiver::send(int frame_id, float packet_collection_time_ms, float decoder_time_ms,
                    float frame_time_ms, int packet_count)
{
    std::vector<uint8_t> packet(21);
    packet[0] = 1;
    memcpy(packet.data() + 1, &frame_id, 4);
    memcpy(packet.data() + 5, &packet_collection_time_ms, 4);
    memcpy(packet.data() + 9, &decoder_time_ms, 4);
    memcpy(packet.data() + 13, &frame_time_ms, 4);
    memcpy(packet.data() + 17, &packet_count, 4);
    socket_.send_to(asio::buffer(packet), remote_endpoint_);
}

void Receiver::send(int frame_id, const std::vector<int>& missing_packet_ids)
{
    std::vector<uint8_t> packet(1 + 4 + 4 + 4 * missing_packet_ids.size());
    int cursor = 0;
    packet[cursor] = 2;
    cursor += 1;

    memcpy(packet.data() + cursor, &frame_id, 4);
    cursor += 4;
    
    int missing_packet_count = missing_packet_ids.size();
    memcpy(packet.data() + cursor, &missing_packet_count, 4);
    cursor += 4;

    for (int i = 0; i < missing_packet_ids.size(); ++i) {
        memcpy(packet.data() + cursor, &missing_packet_ids[i], 4);
        cursor += 4;
    }

    socket_.send_to(asio::buffer(packet), remote_endpoint_);
}
}