#pragma once

#include <vector>

namespace kh
{
class XorPacketCollection
{
public:
    XorPacketCollection(int frame_id, int packet_count);
    int frame_id() { return frame_id_; }
    int packet_count() { return packet_count_; }
    void addPacket(int packet_index, std::vector<uint8_t>&& packet);
    std::vector<std::uint8_t>* TryGetPacket(int packet_index);

private:
    int frame_id_;
    int packet_count_;
    std::vector<std::vector<std::uint8_t>> packets_;
};
}