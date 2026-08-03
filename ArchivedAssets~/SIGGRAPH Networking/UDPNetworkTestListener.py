#!/usr/bin/env python3
import socket
import struct

UDP_IP = "0.0.0.0"  # Listen on all available interfaces.
UDP_PORT = 12345

# Create a UDP socket.
sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
sock.bind((UDP_IP, UDP_PORT))

print(f"Listening for UDP packets on port {UDP_PORT}...")

while True:
    # Receive data from any sender (buffer size of 4096 bytes).
    data, addr = sock.recvfrom(4096)
    print(f"Packet received from {addr}. Packet size: {len(data)} bytes")

    # Ensure there's at least 1 byte for the validity flag.
    if len(data) < 1:
        print("Received packet is too short to contain a validity flag.")
        continue

    # Read the first byte as the validity flag.
    valid_flag = data[0]
    if valid_flag != 1:
        print("Packet marked as invalid. Skipping.")

    # The remaining bytes are the float data.
    float_data = data[1:]

    # Check if the float_data length is a multiple of 4 (size of a float).
    if len(float_data) % 4 != 0:
        print("Float data length is not a multiple of 4. Skipping packet.")
        continue

    # Unpack the byte data into a tuple of floats.
    # "<" indicates little-endian.
    num_floats = len(float_data) // 4
    float_values = list(struct.unpack("<" + "f" * num_floats, float_data))
    print("Valid packet received!")

    if num_floats % 3 != 0:
        print("Number of floats is not a multiple of 3. Cannot apply x-offset properly.")
        continue
    else:
        # Apply a 0.2 offset to each x coordinate.
        for i in range(0, num_floats, 3):
            float_values[i] += 0.2  # Offset the x component.
        print("Modified float values:", float_values)

    # Pack the modified floats back into bytes.
    modified_float_data = struct.pack("<" + "f" * num_floats, *float_values)
    # Prepend the validity flag (assuming it remains valid).
    new_data = bytes([valid_flag]) + modified_float_data

    # Broadcast the modified packet.
    broadcast_endpoint = ("255.255.255.255", UDP_PORT)
    sock.sendto(new_data, broadcast_endpoint)
    print(f"Broadcasted modified packet with offset to {broadcast_endpoint}")
