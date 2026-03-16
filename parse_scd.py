#!/usr/bin/env python3
"""Parse and print all fields of an SCD file (excluding raw audio stream data)."""

import struct
import sys
from typing import List, Optional, Tuple


# ---------------------------------------------------------------------------
# Enums (name lookup helpers)
# ---------------------------------------------------------------------------

SSCF_WAVE_FORMAT = {-1: 'Empty', 1: 'Pcm', 5: 'Atrac3', 6: 'Vorbis',
                    0x0B: 'Xma', 0x0C: 'MsAdPcm', 0x0D: 'Atrac3Too'}
AUDIO_FLAG = {0x01: 'Enabled_Marker', 0x02: 'Mono_Split', 0x01000000: 'Version_Shift'}
SOUND_TYPE = {0: 'Invalid', 1: 'Normal', 2: 'Random', 3: 'Stereo', 4: 'Cycle',
              5: 'Order', 6: 'FourChannelSurround', 7: 'Engine', 8: 'Dialog',
              10: 'FixedPosition', 11: 'DynamixStream', 12: 'GroupRandom',
              13: 'GroupOrder', 14: 'Atomosgear', 15: 'ConditionalJump',
              16: 'Empty', 128: 'MidiMusic'}
SOUND_ATTRIBUTE = {0x0001: 'Loop', 0x0002: 'Reverb', 0x0004: 'Fixed_Volume',
                   0x0008: 'Fixed_Position', 0x0020: 'Music', 0x0040: 'Bypass_PLIIz',
                   0x0080: 'Use_External_Attr', 0x0100: 'Exist_Routing_Setting',
                   0x0200: 'Music_Surround', 0x0400: 'Bus_Ducking',
                   0x0800: 'Acceleration', 0x1000: 'Dynamix_End',
                   0x2000: 'Extra_Desc', 0x4000: 'Dynamix_Plus', 0x8000: 'Atomosgear'}
SOUND_OBJECT_TYPE = {0: 'Null', 1: 'Ambient', 2: 'Direction', 3: 'Point',
                     4: 'PointDir', 5: 'Line', 6: 'Polyline', 7: 'Surface',
                     8: 'BoardObstruction', 9: 'BoxObstruction',
                     10: 'PolylineObstruction', 11: 'Polygon',
                     12: 'BoxExtController', 13: 'LineExtController',
                     14: 'PolygonObstruction'}
TRACK_CMD = {0: 'End', 1: 'Volume', 2: 'Pitch', 3: 'Interval', 4: 'Modulation',
             5: 'ReleaseRate', 6: 'Panning', 7: 'KeyOn', 8: 'RandomVolume',
             9: 'RandomPitch', 10: 'RandomPan', 12: 'KeyOff', 13: 'LoopStart',
             14: 'LoopEnd', 15: 'ExternalAudio', 16: 'EndForLoop',
             17: 'AddInterval', 18: 'Expression', 19: 'Velocity',
             20: 'MidiVolume', 21: 'MidiAddVolume', 22: 'MidiPan',
             23: 'MidiAddPan', 24: 'ModulationType', 25: 'ModulationDepth',
             26: 'ModulationAddDepth', 27: 'ModulationSpeed',
             28: 'ModulationAddSpeed', 29: 'ModulationOff', 30: 'PitchBend',
             31: 'Transpose', 32: 'AddTranspose', 33: 'FrPanning',
             34: 'RandomWait', 35: 'Adsr', 36: 'CutOff', 37: 'Jump',
             38: 'PlayContinueLoop', 39: 'Sweep', 40: 'MidiKeyOnOld',
             41: 'SlurOn', 42: 'SlurOff', 43: 'AutoAdsrEnvelope',
             44: 'MidiExternalAudio', 45: 'Marker', 46: 'InitParams',
             47: 'Version', 48: 'ReverbOn', 49: 'ReverbOff', 50: 'MidiKeyOn',
             51: 'PortamentoOn', 52: 'PortamentoOff', 53: 'MidiEnd',
             54: 'ClearKeyInfo', 55: 'ModulationDepthFade',
             56: 'ModulationSpeedFade', 57: 'AnalysisFlag', 58: 'Config',
             59: 'Filter', 60: 'PlayInnerSound', 61: 'VolumeZeroOne',
             62: 'ZeroOneJump', 63: 'ChannelVolumeZeroOne', 64: 'Unknown64'}
INSERT_EFFECT = {0: 'NoEffect', 1: 'LowPassFilter', 2: 'HighPassFilter',
                 3: 'BandPassFilter', 4: 'BandEliminateFilter',
                 5: 'LowShelvingFilter', 6: 'HighShelvingFilter',
                 7: 'PeakingFilter', 8: 'Equalizer', 9: 'Compressor',
                 10: 'Reverb', 11: 'GranularSynthesizer', 12: 'Delay',
                 13: 'SimpleMeter'}
FILTER_TYPE = {0: 'Bypass', 1: 'LowPass', 2: 'HighPass', 3: 'BandPass',
               4: 'BandEliminate', 5: 'LowShelving', 6: 'HighShelving', 7: 'Peaking'}
SELF_COMMAND = {0: 'None', 1: 'FadeIn', 2: 'ChgPriority', 3: 'FreezePan',
                4: 'Stay', 5: 'ChgPan', 6: 'BanRear', 7: 'NoPlay',
                8: 'ChgDepth', 9: 'MonoSpeaker', 10: 'ChgVolume',
                11: 'ChgBusNo', 12: 'ChgBusVolume'}
TARGET_COMMAND = {0: 'None', 1: 'Stop', 2: 'ChgPriority', 3: 'FadeOut',
                  4: 'ChgPitch', 5: 'PriorityStop', 6: 'ChgPan', 7: 'ChgVolume',
                  8: 'OldStop', 9: 'BanRear', 10: 'ChgDepth', 11: 'ChgBusNo',
                  12: 'LowExternalIdOldStop', 13: 'MinVolumeStop'}
CONDITION_TYPE2 = {0x00: 'None', 0x01: 'Frame', 0x02: 'Volume', 0x03: 'Pan',
                   0x04: 'Count', 0x05: 'Priority', 0x06: 'ExternalId',
                   0x10: 'GT', 0x20: 'LT', 0x30: 'LE', 0x40: 'EQ',
                   0x42: 'Unknown', 0x50: 'NE'}
JOIN_TYPE = {0: 'And', 1: 'Or'}
TRACK_CONFIG_TYPE = {0: 'IntervalType', 1: 'IntervalTypeFloat'}
OSCE_CARRIER = {0: 'Carrier0', 1: 'Carrier1', 2: 'Carrier2', 3: 'Carrier3'}


def flags_str(value: int, flag_dict: dict) -> str:
    active = [name for bit, name in flag_dict.items() if value & bit]
    return f"0x{value:04X} [{', '.join(active) if active else 'none'}]"


def enum_str(value: int, enum_dict: dict) -> str:
    return f"{enum_dict.get(value, f'Unknown({value})')} ({value})"


# ---------------------------------------------------------------------------
# Reader helper
# ---------------------------------------------------------------------------

class Reader:
    def __init__(self, data: bytes):
        self.data = data
        self.pos = 0

    def seek(self, pos: int):
        self.pos = pos

    def tell(self) -> int:
        return self.pos

    def read_bytes(self, n: int) -> bytes:
        v = self.data[self.pos:self.pos + n]
        self.pos += n
        return v

    def i8(self) -> int:
        v, = struct.unpack_from('<b', self.data, self.pos); self.pos += 1; return v
    def u8(self) -> int:
        v, = struct.unpack_from('<B', self.data, self.pos); self.pos += 1; return v
    def i16(self) -> int:
        v, = struct.unpack_from('<h', self.data, self.pos); self.pos += 2; return v
    def u16(self) -> int:
        v, = struct.unpack_from('<H', self.data, self.pos); self.pos += 2; return v
    def i32(self) -> int:
        v, = struct.unpack_from('<i', self.data, self.pos); self.pos += 4; return v
    def u32(self) -> int:
        v, = struct.unpack_from('<I', self.data, self.pos); self.pos += 4; return v
    def f32(self) -> float:
        v, = struct.unpack_from('<f', self.data, self.pos); self.pos += 4; return v
    def float4(self) -> Tuple[float, float, float, float]:
        v = struct.unpack_from('<4f', self.data, self.pos); self.pos += 16; return v
    def float2(self) -> Tuple[float, float]:
        v = struct.unpack_from('<2f', self.data, self.pos); self.pos += 8; return v
    def pad_to(self, alignment: int):
        rem = self.pos % alignment
        if rem != 0:
            self.pos += alignment - rem


# ---------------------------------------------------------------------------
# Parsing helpers
# ---------------------------------------------------------------------------

def p(label: str, value, indent: int):
    print(f"{'  ' * indent}{label}: {value}")

def section(title: str, indent: int):
    print(f"{'  ' * indent}--- {title} ---")


def read_offsets(r: Reader, count: int, first_offset: list) -> List[int]:
    offsets = []
    for _ in range(count):
        o = r.i32()
        if o > 0 and (first_offset[0] == -1 or o < first_offset[0]):
            first_offset[0] = o
        offsets.append(o)
    r.pad_to(16)
    return offsets


# ---------------------------------------------------------------------------
# Audio Marker
# ---------------------------------------------------------------------------

def parse_audio_marker(r: Reader, sample_rate: int, indent: int):
    section("AudioMarker", indent)
    marker_id = r.read_bytes(4).decode('ascii', errors='replace')
    size = r.u32()
    loop_start_samples = r.i32()
    loop_end_samples = r.i32()
    num_markers = r.i32()
    p("Id", marker_id, indent + 1)
    p("Size", size, indent + 1)
    p("LoopStart (samples)", loop_start_samples, indent + 1)
    if sample_rate:
        p("LoopStart (seconds)", loop_start_samples / sample_rate, indent + 1)
    p("LoopEnd (samples)", loop_end_samples, indent + 1)
    if sample_rate:
        p("LoopEnd (seconds)", loop_end_samples / sample_rate, indent + 1)
    p("NumMarkers", num_markers, indent + 1)
    for i in range(num_markers):
        m = r.i32()
        p(f"  Marker[{i}] (samples)", m, indent + 1)
        if sample_rate:
            p(f"  Marker[{i}] (seconds)", m / sample_rate, indent + 1)
    r.pad_to(16)


# ---------------------------------------------------------------------------
# Audio Entry
# ---------------------------------------------------------------------------

def parse_audio_entry(r: Reader, idx: int, offset: int, indent: int = 0):
    r.seek(offset)
    section(f"AudioEntry[{idx}] @ 0x{offset:X}", indent)
    i = indent + 1

    data_length = r.i32()
    num_channels = r.i32()
    sample_rate = r.i32()
    fmt = r.i32()
    loop_start = r.i32()
    loop_end = r.i32()
    sub_info_size = r.i32()
    flags = r.i32()

    p("DataLength", data_length, i)
    p("NumChannels", num_channels, i)
    p("SampleRate", sample_rate, i)
    p("Format", enum_str(fmt, SSCF_WAVE_FORMAT), i)
    p("LoopStart", loop_start, i)
    p("LoopEnd", loop_end, i)
    p("SubInfoSize", sub_info_size, i)
    p("Flags", flags_str(flags, AUDIO_FLAG), i)

    if data_length == 0:
        p("[Empty audio entry]", "", i)
        return

    has_marker = bool(flags & 0x01)
    aux_data_size = 0
    if has_marker:
        parse_audio_marker(r, sample_rate, i)
        # Recompute marker size from current position (marker already read)
        # We track it from sub_info_size: aux_data_size = total - data body
    # Recalculate aux_data_size as bytes consumed so far past the 32-byte header
    # We know we're now past the marker (if any).

    # --- Format-specific sub-info (metadata only, skip audio data) ---
    if fmt == 0x06:  # Vorbis
        section("VorbisInfo", i)
        encode_mode = r.i16()
        encode_byte = r.i16()
        xor_offset = r.i32()
        xor_size = r.i32()
        seek_step = r.f32()
        seek_table_size_bytes = r.i32()

        # Legacy SCD detection: if seekTableSize bytes spell "...vor" in ASCII
        raw = seek_table_size_bytes.to_bytes(4, 'little')
        if raw[1:] == b'vor':
            p("LegacyFormat", True, i + 1)
            r.read_bytes(0x35C)  # skip legacy header
            p("[Skipping legacy audio data]", f"{data_length + 0x10} bytes", i + 1)
            r.read_bytes(data_length + 0x10)
            return

        vorbis_header_size = r.i32()
        unknown1 = r.i32()
        unknown2 = r.i32()

        p("EncodeMode", encode_mode, i + 1)
        p("EncodeByte", encode_byte, i + 1)
        p("XorOffset", xor_offset, i + 1)
        p("XorSize", xor_size, i + 1)
        p("SeekStep", seek_step, i + 1)
        p("SeekTableSize", seek_table_size_bytes, i + 1)
        p("VorbisHeaderSize", vorbis_header_size, i + 1)
        p("Unknown1", unknown1, i + 1)
        p("Unknown2", unknown2, i + 1)

        num_seek_entries = seek_table_size_bytes // 4
        seek_table = [r.i32() for _ in range(num_seek_entries)]
        p("SeekTable", seek_table[:16], i + 1)
        if len(seek_table) > 16:
            p(f"  ... ({len(seek_table)} entries total)", "", i + 1)

        p("[Skipping VorbisHeader]", f"{vorbis_header_size} bytes", i + 1)
        r.read_bytes(vorbis_header_size)  # encoded Ogg headers
        p("[Skipping audio stream]", f"{data_length} bytes", i + 1)
        r.read_bytes(data_length)

    elif fmt == 0x0C:  # MsAdPcm
        section("AdpcmInfo", i)
        wave_header_size = sub_info_size - aux_data_size
        wave_header = r.read_bytes(wave_header_size)
        p("WaveHeader (hex)", wave_header.hex(), i + 1)
        p("[Skipping audio stream]", f"{data_length} bytes", i + 1)
        r.read_bytes(data_length)

    else:
        p(f"[Skipping unknown format sub-info + audio]",
          f"{sub_info_size + data_length} bytes", i)
        r.read_bytes(sub_info_size + data_length)


# ---------------------------------------------------------------------------
# Sound Routing Info
# ---------------------------------------------------------------------------

def parse_routing_info(r: Reader, indent: int):
    section("RoutingInfo", indent)
    i = indent + 1
    data_size = r.u32()
    send_count = r.u8()
    r.read_bytes(11)  # reserve

    p("DataSize", data_size, i)
    p("SendCount", send_count, i)

    for s in range(send_count):
        section(f"SendInfo[{s}]", i)
        target = r.u8()
        r.read_bytes(3)
        volume = r.f32()
        r.read_bytes(8)
        p("Target", target, i + 1)
        p("Volume", volume, i + 1)

    # SoundEffectParam (144 bytes total)
    section("EffectParam", i)
    effect_type = r.u8()
    r.read_bytes(3)
    p("Type", enum_str(effect_type, INSERT_EFFECT), i + 1)

    for f in range(8):
        freq = r.f32()
        invq = r.f32()
        gain = r.f32()
        ftype = r.i32()
        p(f"Filter[{f}]", f"freq={freq:.4f} invq={invq:.4f} gain={gain:.4f} "
          f"type={enum_str(ftype, FILTER_TYPE)}", i + 1)

    num_filters = r.i32()
    r.read_bytes(8)
    p("NumFilters", num_filters, i + 1)


# ---------------------------------------------------------------------------
# Sound Entry
# ---------------------------------------------------------------------------

def parse_sound_entry(r: Reader, idx: int, offset: int, indent: int = 0):
    r.seek(offset)
    section(f"SoundEntry[{idx}] @ 0x{offset:X}", indent)
    i = indent + 1

    track_count = r.u8()
    bus_number = r.u8()
    priority = r.u8()
    sound_type = r.u8()
    attributes = r.i16()
    volume = r.f32()
    local_number = r.i16()
    user_id = r.u8()
    play_history = r.u8()  # signed in original but read as byte

    p("TrackCount", track_count, i)
    p("BusNumber", bus_number, i)
    p("Priority", priority, i)
    p("Type", enum_str(sound_type, SOUND_TYPE), i)
    p("Attributes", flags_str(attributes & 0xFFFF, SOUND_ATTRIBUTE), i)
    p("Volume", volume, i)
    p("LocalNumber", local_number, i)
    p("UserId", user_id, i)
    p("PlayHistory", play_history, i)

    routing_enabled = bool(attributes & 0x0100)
    acceleration_enabled = bool(attributes & 0x0800)
    atomos_enabled = bool(attributes & 0x8000)
    extra_enabled = bool(attributes & 0x2000)
    random_tracks = sound_type in (2, 4, 12, 13)  # Random, Cycle, GroupRandom, GroupOrder
    is_empty_loop = sound_type == 16 and bool(attributes & 0x0001)

    if routing_enabled:
        parse_routing_info(r, i)

    # BusDucking (always present)
    section("BusDucking", i)
    bd_size = r.u8()
    bd_number = r.u8()
    r.read_bytes(2)
    bd_fade_time = r.i32()
    bd_volume = r.f32()
    r.u32()  # reserve
    p("Size", bd_size, i + 1)
    p("Number", bd_number, i + 1)
    p("FadeTime", bd_fade_time, i + 1)
    p("Volume", bd_volume, i + 1)

    if acceleration_enabled:
        section("Acceleration", i)
        ac_version = r.u8()
        ac_size = r.u8()
        ac_num = r.u8()
        r.read_bytes(13)  # reserve (1 + 4*3)
        p("Version", ac_version, i + 1)
        p("Size", ac_size, i + 1)
        p("NumAcceleration", ac_num, i + 1)
        for a in range(4):
            ai_version = r.u8()
            ai_size = r.u8()
            r.read_bytes(2)
            ai_vol = r.f32()
            ai_up = r.i32()
            ai_down = r.i32()
            p(f"Accel[{a}]", f"version={ai_version} vol={ai_vol:.4f} "
              f"upTime={ai_up} downTime={ai_down}", i + 1)

    if atomos_enabled:
        section("Atomos", i)
        at_version = r.u8()
        r.u8()  # reserved
        at_size = r.u16()
        at_min = r.i16()
        at_max = r.i16()
        r.read_bytes(8)
        p("Version", at_version, i + 1)
        p("Size", at_size, i + 1)
        p("MinPeople", at_min, i + 1)
        p("MaxPeople", at_max, i + 1)

    if extra_enabled:
        section("Extra", i)
        ex_version = r.u8()
        r.u8()
        ex_size = r.u16()
        ex_play_len = r.i32()
        r.read_bytes(8)
        p("Version", ex_version, i + 1)
        p("Size", ex_size, i + 1)
        p("PlayTimeLength", ex_play_len, i + 1)

    # Bypass (always present)
    section("BypassPLIIz", i)
    bp_u1 = r.i16()
    bp_u2 = r.i16()
    bp_u3 = r.u32()
    bp_u4 = r.f32()
    bp_u5 = r.u32()
    bp_u6 = r.u32()
    bp_u7 = r.f32()
    bp_u8 = r.u32()
    bp_u9 = r.u32()
    p("Unknown1", bp_u1, i + 1)
    p("Unknown2", bp_u2, i + 1)
    p("Unknown3", bp_u3, i + 1)
    p("Unknown4", bp_u4, i + 1)
    p("Unknown5", bp_u5, i + 1)
    p("Unknown6", bp_u6, i + 1)
    p("Unknown7", bp_u7, i + 1)
    p("Unknown8", bp_u8, i + 1)
    p("Unknown9", bp_u9, i + 1)

    if is_empty_loop:
        section("EmptyLoop", i)
        p("Unknown1", r.i32(), i + 1)
        p("Unknown2", r.i32(), i + 1)

    if random_tracks:
        section("RandomTracks", i)
        for t in range(track_count):
            track_idx = r.i16()
            audio_idx = r.i16()
            lim_x = r.i16()
            lim_y = r.i16()
            p(f"Track[{t}]", f"trackIdx={track_idx} audioIdx={audio_idx} "
              f"limit=({lim_x},{lim_y})", i + 1)
        if sound_type == 4:  # Cycle
            p("CycleInterval", r.i32(), i + 1)
            p("CycleNumPlayTrack", r.i16(), i + 1)
            p("CycleRange", r.i16(), i + 1)
    else:
        section("Tracks", i)
        for t in range(track_count):
            track_idx = r.i16()
            audio_idx = r.i16()
            p(f"Track[{t}]", f"trackIdx={track_idx} audioIdx={audio_idx}", i + 1)


# ---------------------------------------------------------------------------
# Track Entry
# ---------------------------------------------------------------------------

def parse_track_entry(r: Reader, idx: int, offset: int, indent: int = 0):
    r.seek(offset)
    section(f"TrackEntry[{idx}] @ 0x{offset:X}", indent)
    i = indent + 1
    item_num = 0
    TERMINAL = {0, 53, 16}  # End, MidiEnd, EndForLoop

    while True:
        cmd = r.i16()
        cmd_name = TRACK_CMD.get(cmd, f"Unknown({cmd})")
        data_str = ""

        if cmd == 1 or cmd == 2 or cmd == 6 or cmd == 33 or cmd == 35:  # TrackParamData
            val = r.f32(); time = r.i32()
            data_str = f"value={val:.4f} time={time}"
        elif cmd == 3 or cmd == 5 or cmd == 62:  # TrackIntData
            data_str = f"value={r.i32()}"
        elif cmd == 4:  # TrackModulationData
            carrier = r.u8(); mod = r.u8(); curve = r.u8(); r.u8()
            depth = r.f32(); rate = r.i32()
            data_str = f"carrier={carrier} modulator={mod} curve={curve} depth={depth:.4f} rate={rate}"
        elif cmd in (8, 9, 10):  # TrackRandomData
            upper = r.f32(); lower = r.f32()
            data_str = f"upper={upper:.4f} lower={lower:.4f}"
        elif cmd in (13, 34):  # TrackInt2Data
            data_str = f"v1={r.i32()} v2={r.i32()}"
        elif cmd == 15 or cmd == 44:  # TrackExternalAudioData
            bank = r.i16(); idx2 = r.i16()
            extra = []
            if idx2 < 0:
                extra = [r.i16() for _ in range(-idx2)]
            data_str = f"bank={bank} index={idx2}" + (f" indices={extra}" if extra else "")
        elif cmd in (17, 19, 21, 23, 32, 48):  # TrackFloatData
            data_str = f"value={r.f32():.4f}"
        elif cmd in (18, 20, 22, 30, 31, 50):  # TrackFloat2Data
            data_str = f"v1={r.f32():.4f} v2={r.f32():.4f}"
        elif cmd == 24:  # TrackModulationTypeData
            data_str = f"carrier={r.i32()} modulator={r.i32()}"
        elif cmd in (25, 26):  # TrackModulationDepthData
            data_str = f"carrier={r.i32()} depth={r.f32():.4f}"
        elif cmd in (27, 28):  # TrackModulationSpeedData
            data_str = f"carrier={r.i32()} speed={r.i32()}"
        elif cmd == 29:  # TrackModulationOffData
            data_str = f"carrier={r.i32()}"
        elif cmd == 37:  # TrackJumpData
            cond = r.i32(); off = r.i32()
            data_str = f"condition={cond} offset={off}"
        elif cmd == 39:  # TrackSweepData
            data_str = f"pitch={r.f32():.4f} time={r.i32()}"
        elif cmd == 43:  # TrackAutoAdsrEnvelopeData
            data_str = (f"attack={r.i32()} decay={r.i32()} "
                        f"sustain={r.i32()} release={r.i32()}")
        elif cmd == 47:  # TrackShortData
            data_str = f"value={r.i16()}"
        elif cmd == 51:  # TrackPortamentoData
            data_str = f"time={r.i32()} pitch={r.f32():.4f}"
        elif cmd == 55:  # TrackModulationDepthFadeData
            data_str = f"carrier={r.i32()} depth={r.f32():.4f} fadeTime={r.i32()}"
        elif cmd == 56:  # TrackModulationSpeedFadeData
            data_str = f"carrier={r.i32()} speed={r.i32()} fadeTime={r.i32()}"
        elif cmd == 57:  # TrackAnalysisFlagData
            count = r.i16()
            flags = [r.i16() for _ in range(count)]
            data_str = f"data={flags}"
        elif cmd == 58:  # TrackConfigData
            cfg_type = r.i16(); count = r.u16()
            if cfg_type == 1:  # IntervalTypeFloat
                data_str = f"type={enum_str(cfg_type, TRACK_CONFIG_TYPE)} count={count} value={r.u16()}"
            elif cfg_type > 1:
                vals = [r.u16() for _ in range(count)]
                data_str = f"type={enum_str(cfg_type, TRACK_CONFIG_TYPE)} count={count} data={vals}"
            else:
                data_str = f"type={enum_str(cfg_type, TRACK_CONFIG_TYPE)} count={count}"
        elif cmd == 59:  # TrackFilterData
            ftype = r.i32(); freq = r.f32(); invq = r.f32(); gain = r.f32()
            data_str = f"type={enum_str(ftype, FILTER_TYPE)} freq={freq:.4f} invq={invq:.4f} gain={gain:.4f}"
        elif cmd == 60:  # TrackPlayInnerSoundData
            data_str = f"bank={r.i16()} soundIndex={r.i16()}"
        elif cmd == 61:  # TrackVolumeZeroOneData
            ver = r.u8(); r.u8(); hsize = r.i16(); count = r.i16()
            pts = [(r.i16(), r.i16()) for _ in range(count)]
            data_str = f"version={ver} headerSize={hsize} points={pts}"
        elif cmd == 63:  # TrackChannelVolumeZeroOneData
            ver = r.u8(); r.u8(); hsize = r.i16(); count = r.i16()
            channels = []
            for _ in range(count):
                cv = r.u8(); r.u8(); ch_hsize = r.i16(); ch_count = r.i16()
                ch_pts = [(r.i16(), r.i16()) for _ in range(ch_count)]
                channels.append(f"version={cv} headerSize={ch_hsize} points={ch_pts}")
            data_str = f"version={ver} headerSize={hsize} channels={channels}"
        elif cmd == 64:  # TrackUnknown64Data
            ver = r.u8(); count = r.u8(); unk1 = r.i16()
            items = []
            for _ in range(count):
                bank = r.i16(); ix = r.i16(); u1 = r.i32(); u2 = r.f32()
                items.append(f"bank={bank} idx={ix} unk1={u1} unk2={u2:.4f}")
            data_str = f"version={ver} unk1={unk1} items={items}"

        p(f"Item[{item_num}]", f"{cmd_name}" + (f": {data_str}" if data_str else ""), i)
        item_num += 1

        if cmd in TERMINAL:
            break


# ---------------------------------------------------------------------------
# Attribute Entry
# ---------------------------------------------------------------------------

def parse_result_command(r: Reader, indent: int):
    self_cmd = r.u8()
    target_cmd = r.u8()
    r.read_bytes(2)
    self_arg = r.i32()
    target_arg = r.i32()
    p("SelfCommand", enum_str(self_cmd, SELF_COMMAND), indent)
    p("TargetCommand", enum_str(target_cmd, TARGET_COMMAND), indent)
    p("SelfArgument", self_arg, indent)
    p("TargetArgument", target_arg, indent)


def parse_extend_data(r: Reader, num: int, indent: int):
    section(f"ExtendData[{num}]", indent)
    i = indent + 1
    first_cond = r.u8()
    second_cond = r.u8()
    join_type = r.u8()
    num_conds = r.u8()
    self_arg = r.i32()
    target_arg_raw = r.i32()

    p("FirstCondition", f"0x{first_cond:02X}", i)
    p("SecondCondition", enum_str(second_cond, CONDITION_TYPE2), i)
    p("JoinType", enum_str(join_type, JOIN_TYPE), i)
    p("NumberOfConditions", num_conds, i)
    p("SelfArgument", self_arg, i)

    if second_cond == 0x42:  # Unknown -> float
        val, = struct.unpack('<f', target_arg_raw.to_bytes(4, 'little', signed=True))
        p("TargetArgument (float)", val, i)
    else:
        p("TargetArgument (int)", target_arg_raw, i)

    section("Result", i)
    parse_result_command(r, i + 1)


def parse_attribute_entry(r: Reader, idx: int, offset: int, indent: int = 0):
    r.seek(offset)
    section(f"AttributeEntry[{idx}] @ 0x{offset:X}", indent)
    i = indent + 1

    version = r.u8()
    r.u8()  # reserved
    attr_id = r.i16()
    search_attr_id = r.i16()
    cond_first = r.u8()
    arg_count = r.u8()
    sound_label_low = r.i32()
    sound_label_high = r.i32()

    p("Version", version, i)
    p("AttributeId", attr_id, i)
    p("SearchAttributeId", search_attr_id, i)
    p("ConditionFirst", cond_first, i)
    p("ArgumentCount", arg_count, i)
    p("SoundLabelLow", sound_label_low, i)
    p("SoundLabelHigh", sound_label_high, i)

    section("ResultFirst", i)
    parse_result_command(r, i + 1)

    for e in range(4):
        parse_extend_data(r, e, i)


# ---------------------------------------------------------------------------
# Layout Entry
# ---------------------------------------------------------------------------

def parse_layout_data(r: Reader, obj_type: int, indent: int):
    if obj_type == 0:  # Null
        return

    elif obj_type == 1:  # Ambient
        section("AmbientData", indent)
        i = indent + 1
        p("Volume", r.f32(), i)
        p("Pitch", r.f32(), i)
        p("ReverbFac", r.f32(), i)
        p("DirectVolume1", r.float4(), i)
        p("DirectVolume2", r.float4(), i)
        r.read_bytes(4)

    elif obj_type == 2:  # Direction
        section("DirectionData", indent)
        i = indent + 1
        p("Volume", r.f32(), i)
        p("Pitch", r.f32(), i)
        p("ReverbFac", r.f32(), i)
        p("Direction", r.f32(), i)
        p("RotSpeed", r.f32(), i)
        r.read_bytes(12)

    elif obj_type == 3:  # Point
        section("PointData", indent)
        i = indent + 1
        p("Position", r.float4(), i)
        p("MaxRange", r.f32(), i)
        p("MinRange", r.f32(), i)
        p("Height", r.float2(), i)
        p("RangeVolume", r.f32(), i)
        p("Volume", r.f32(), i)
        p("Pitch", r.f32(), i)
        p("ReverbFac", r.f32(), i)
        p("DopplerFac", r.f32(), i)
        p("CenterFac", r.f32(), i)
        p("InteriorFac", r.f32(), i)
        p("Direction", r.f32(), i)
        p("NearFadeStart", r.f32(), i)
        p("NearFadeEnd", r.f32(), i)
        p("FarDelayFac", r.f32(), i)
        p("Environment", f"0x{r.u8():02X}", i)
        p("Flag", f"0x{r.u8():02X}", i)
        r.read_bytes(2)
        p("LowerLimit", r.f32(), i)
        p("FadeInTime", r.i16(), i)
        p("FadeOutTime", r.i16(), i)
        p("ConvergenceFac", r.f32(), i)
        r.read_bytes(4)

    elif obj_type == 4:  # PointDir
        section("PointDirData", indent)
        i = indent + 1
        p("Position", r.float4(), i)
        p("Direction", r.float4(), i)
        p("RangeX", r.f32(), i)
        p("RangeY", r.f32(), i)
        p("MaxRange", r.f32(), i)
        p("MinRange", r.f32(), i)
        p("Height", r.float2(), i)
        p("RangeVolume", r.f32(), i)
        p("Volume", r.f32(), i)
        p("Pitch", r.f32(), i)
        p("ReverbFac", r.f32(), i)
        p("DopplerFac", r.f32(), i)
        p("InteriorFac", r.f32(), i)
        p("FixedDirection", r.f32(), i)
        r.read_bytes(12)

    elif obj_type == 5:  # Line
        section("LineData", indent)
        i = indent + 1
        p("StartPosition", r.float4(), i)
        p("EndPosition", r.float4(), i)
        p("MaxRange", r.f32(), i)
        p("MinRange", r.f32(), i)
        p("Height", r.float2(), i)
        p("RangeVolume", r.f32(), i)
        p("Volume", r.f32(), i)
        p("Pitch", r.f32(), i)
        p("ReverbFac", r.f32(), i)
        p("DopplerFac", r.f32(), i)
        p("InteriorFac", r.f32(), i)
        p("Direction", r.f32(), i)
        r.read_bytes(4)

    elif obj_type == 6:  # Polyline
        section("PolylineData", indent)
        i = indent + 1
        # 16 float4 positions in reverse order
        positions = [r.float4() for _ in range(16)]
        for pi, pos in enumerate(reversed(positions)):
            p(f"Position[{pi}]", pos, i)
        p("MaxRange", r.f32(), i)
        p("MinRange", r.f32(), i)
        p("Height", r.float2(), i)
        p("RangeVolume", r.f32(), i)
        p("Volume", r.f32(), i)
        p("Pitch", r.f32(), i)
        p("ReverbFac", r.f32(), i)
        p("DopplerFac", r.f32(), i)
        p("VertexCount", r.u8(), i)
        r.read_bytes(3)
        p("InteriorFac", r.f32(), i)
        p("Direction", r.f32(), i)

    elif obj_type == 7:  # Surface
        section("SurfaceData", indent)
        i = indent + 1
        p("Position1", r.float4(), i)
        p("Position2", r.float4(), i)
        p("Position3", r.float4(), i)
        p("Position4", r.float4(), i)
        p("MaxRange", r.f32(), i)
        p("MinRange", r.f32(), i)
        p("Height", r.float2(), i)
        p("RangeVolume", r.f32(), i)
        p("Volume", r.f32(), i)
        p("Pitch", r.f32(), i)
        p("ReverbFac", r.f32(), i)
        p("DopplerFac", r.f32(), i)
        p("InteriorFac", r.f32(), i)
        p("Direction", r.f32(), i)
        p("SubSoundType", r.u8(), i)
        p("Flag", f"0x{r.u8():02X}", i)
        r.read_bytes(2)
        p("RotSpeed", r.f32(), i)
        r.read_bytes(12)

    elif obj_type == 8:  # BoardObstruction
        section("BoardObstructionData", indent)
        i = indent + 1
        for pi in range(4):
            p(f"Position{pi+1}", r.float4(), i)
        p("ObstacleFac", r.f32(), i)
        p("HiCutFac", r.f32(), i)
        p("Flags", f"0x{r.u8():02X}", i)
        r.read_bytes(3)
        p("OpenTime", r.i16(), i)
        p("CloseTime", r.i16(), i)

    elif obj_type == 9:  # BoxObstruction
        section("BoxObstructionData", indent)
        i = indent + 1
        for pi in range(4):
            p(f"Position{pi+1}", r.float4(), i)
        p("Height", r.float2(), i)
        p("ObstacleFac", r.f32(), i)
        p("HiCutFac", r.f32(), i)
        p("Flags", f"0x{r.u8():02X}", i)
        r.read_bytes(3)
        p("FadeRange", r.f32(), i)
        p("OpenTime", r.i16(), i)
        p("CloseTime", r.i16(), i)
        r.read_bytes(4)

    elif obj_type == 10:  # PolylineObstruction
        section("PolylineObstructionData", indent)
        i = indent + 1
        positions = [r.float4() for _ in range(16)]
        for pi, pos in enumerate(reversed(positions)):
            p(f"Position[{pi}]", pos, i)
        p("Height", r.float2(), i)
        p("ObstacleFac", r.f32(), i)
        p("HiCutFac", r.f32(), i)
        p("Flags", f"0x{r.u8():02X}", i)
        p("VertexCount", r.u8(), i)
        r.read_bytes(2)
        p("Width", r.f32(), i)
        p("FadeRange", r.f32(), i)
        p("OpenTime", r.i16(), i)
        p("CloseTime", r.i16(), i)

    elif obj_type == 11:  # Polygon
        section("PolygonData", indent)
        i = indent + 1
        p("MaxRange", r.f32(), i)
        p("MinRange", r.f32(), i)
        p("Height", r.float2(), i)
        p("RangeVolume", r.f32(), i)
        p("Volume", r.f32(), i)
        p("Pitch", r.f32(), i)
        p("ReverbFac", r.f32(), i)
        p("DopplerFac", r.f32(), i)
        p("InteriorFac", r.f32(), i)
        p("Direction", r.f32(), i)
        p("SubSoundType", r.u8(), i)
        p("Flag", f"0x{r.u8():02X}", i)
        p("VertexCount", r.u8(), i)
        r.read_bytes(1)
        p("RotSpeed", r.f32(), i)
        r.read_bytes(12)
        for pi in range(32):
            p(f"Position[{pi}]", r.float4(), i)

    elif obj_type == 13:  # LineExtController
        section("LineExtControllerData", indent)
        i = indent + 1
        p("StartPosition", r.float4(), i)
        p("EndPosition", r.float4(), i)
        p("MaxRange", r.f32(), i)
        p("MinRange", r.f32(), i)
        p("Height", r.float2(), i)
        p("RangeVolume", r.f32(), i)
        p("Volume", r.f32(), i)
        p("LowerLimit", r.f32(), i)
        p("FunctionNumber", r.i32(), i)
        p("CalcType", r.u8(), i)
        r.read_bytes(19)

    elif obj_type == 14:  # PolygonObstruction
        section("PolygonObstructionData", indent)
        i = indent + 1
        positions = [r.float4() for _ in range(32)]
        for pi, pos in enumerate(reversed(positions)):
            p(f"Position[{pi}]", pos, i)
        p("ObstacleFac", r.f32(), i)
        p("HiCutFac", r.f32(), i)
        p("Flags", f"0x{r.u8():02X}", i)
        p("VertexCount", r.u8(), i)
        r.read_bytes(2)
        p("OpenTime", r.i16(), i)
        p("CloseTime", r.i16(), i)

    else:
        p(f"[Unhandled layout type {obj_type}]", "", indent)


def parse_layout_entry(r: Reader, idx: int, offset: int, indent: int = 0):
    r.seek(offset)
    section(f"LayoutEntry[{idx}] @ 0x{offset:X}", indent)
    i = indent + 1

    size = r.u16()
    obj_type = r.u8()
    version = r.u8()
    flag1 = r.u8()
    group_number = r.u8()
    local_id = r.i16()
    bank_id = r.i32()
    flag2 = r.u8()
    reverb_type = r.u8()
    ab_group = r.i16()
    volume = r.float4()

    p("Size", size, i)
    p("Type", enum_str(obj_type, SOUND_OBJECT_TYPE), i)
    p("Version", version, i)
    p("Flag1", f"0x{flag1:02X}", i)
    p("GroupNumber", group_number, i)
    p("LocalId", local_id, i)
    p("BankId", bank_id, i)
    p("Flag2", f"0x{flag2:02X}", i)
    p("ReverbType", reverb_type, i)
    p("AbGroupNumber", ab_group, i)
    p("Volume", volume, i)

    parse_layout_data(r, obj_type, i)


# ---------------------------------------------------------------------------
# Main parse
# ---------------------------------------------------------------------------

def parse_scd(path: str):
    with open(path, 'rb') as f:
        data = f.read()

    r = Reader(data)

    # --- ScdHeader ---
    section(f"ScdHeader", 0)
    magic = r.i32()
    section_type = r.i32()
    sedb_version = r.i32()
    endian = r.u8()
    alignment_bits = r.u8()
    header_size = r.i16()
    file_size = r.i32()
    r.read_bytes(28)  # UnkPadding

    magic_str = magic.to_bytes(4, 'little').decode('ascii', errors='replace')
    p("Magic", f"0x{magic:08X}  '{magic_str}'", 1)
    p("SectionType", f"0x{section_type:08X}", 1)
    p("SedbVersion", sedb_version, 1)
    p("Endian", endian, 1)
    p("AlignmentBits", alignment_bits, 1)
    p("HeaderSize", f"0x{header_size:04X} (expected 0x0030)", 1)
    p("FileSize", f"{file_size} bytes", 1)

    # --- ScdReader (offset table header) ---
    section("OffsetTable", 0)
    sound_count = r.i16()
    track_count = r.i16()
    audio_count = r.i16()
    unknown_offset = r.i16()
    track_offset = r.i32()
    audio_offset = r.i32()
    layout_offset = r.i32()
    routing_offset = r.i32()
    attribute_offset = r.i32()
    eof_padding_size = r.i32()

    p("SoundCount", sound_count, 1)
    p("TrackCount", track_count, 1)
    p("AudioCount", audio_count, 1)
    p("UnknownOffset", unknown_offset, 1)
    p("TrackOffset", f"0x{track_offset:08X}", 1)
    p("AudioOffset", f"0x{audio_offset:08X}", 1)
    p("LayoutOffset", f"0x{layout_offset:08X}", 1)
    p("RoutingOffset", f"0x{routing_offset:08X}", 1)
    p("AttributeOffset", f"0x{attribute_offset:08X}", 1)
    p("EofPaddingSize", eof_padding_size, 1)

    # --- Offset arrays ---
    first_offset = [-1]
    sound_offsets = read_offsets(r, sound_count, first_offset)
    track_offsets = read_offsets(r, track_count, first_offset)
    audio_offsets = read_offsets(r, audio_count, first_offset)

    layout_offsets = []
    if layout_offset != 0:
        layout_offsets = read_offsets(r, sound_count, first_offset)

    attribute_offsets = []
    if attribute_offset != 0:
        attr_count = (first_offset[0] - attribute_offset) // 4
        if attr_count > 0:
            attribute_offsets = read_offsets(r, attr_count, first_offset)

    section("OffsetArrays", 0)
    p("SoundOffsets", [f"0x{o:X}" for o in sound_offsets], 1)
    p("TrackOffsets", [f"0x{o:X}" for o in track_offsets], 1)
    p("AudioOffsets", [f"0x{o:X}" for o in audio_offsets], 1)
    if layout_offsets:
        p("LayoutOffsets", [f"0x{o:X}" for o in layout_offsets], 1)
    if attribute_offsets:
        p("AttributeOffsets", [f"0x{o:X}" for o in attribute_offsets], 1)

    # --- Parse entries ---
    for idx, offset in enumerate(o for o in layout_offsets if o != 0):
        parse_layout_entry(r, idx, offset)

    for idx, offset in enumerate(o for o in sound_offsets if o != 0):
        parse_sound_entry(r, idx, offset)

    for idx, offset in enumerate(o for o in track_offsets if o != 0):
        parse_track_entry(r, idx, offset)

    for idx, offset in enumerate(o for o in attribute_offsets if o != 0):
        parse_attribute_entry(r, idx, offset)

    for idx, offset in enumerate(o for o in audio_offsets if o != 0):
        parse_audio_entry(r, idx, offset)


if __name__ == "__main__":
    if len(sys.argv) != 2:
        print(f"Usage: {sys.argv[0]} <file.scd>")
        sys.exit(1)
    parse_scd(sys.argv[1])
