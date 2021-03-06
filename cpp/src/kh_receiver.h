#pragma once

#include <optional>
#include <vector>
#include <asio.hpp>

namespace kh
{
class Receiver
{
public:
    Receiver(asio::io_context& io_context, int receive_buffer_size);
    void ping(std::string ip_address, int port);
    std::optional<std::vector<uint8_t>> receive();
    void send(int frame_id, float packet_collection_time_ms, float decoder_time_ms,
              float frame_time_ms, int packet_count);
    void send(int frame_id, const std::vector<int>& missing_frame_ids);

private:
    asio::ip::udp::socket socket_;
    asio::ip::udp::endpoint remote_endpoint_;
};
}