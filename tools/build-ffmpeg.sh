#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# Build mínima de FFmpeg para Screen Recorder (ejecutar en MSYS2 MINGW64).
#
# Genera un ffmpeg.exe pequeño (~20-35 MB) con SOLO lo que usa el software:
#   - Captura: ddagrab (Desktop Duplication) + gdigrab (respaldo)
#   - Audio:   dshow (micrófono) + wasapi (respaldo) ; el loopback real va por stdin (pipe)
#   - Video:   h264_qsv (Intel), h264_nvenc (NVIDIA), h264_amf (AMD), libx264 (CPU)
#   - Audio:   aac
#   - Muxers:  matroska (grabación MKV) y mov/mp4 (conversión verificada)
#   - Filtros: ddagrab, scale, scale_qsv, vpp_qsv, hwdownload, hwupload, format,
#              aresample, amix, anullsrc, null (para la verificación de reproducibilidad)
#
# Uso:
#   1) Instalar MSYS2 (https://www.msys2.org/) y abrir "MSYS2 MINGW64".
#   2) Instalar dependencias (una vez):
#        pacman -S --needed --noconfirm \
#          mingw-w64-x86_64-toolchain mingw-w64-x86_64-nasm mingw-w64-x86_64-yasm \
#          mingw-w64-x86_64-pkgconf mingw-w64-x86_64-libx264 \
#          mingw-w64-x86_64-onevpl mingw-w64-x86_64-nv-codec-headers \
#          mingw-w64-x86_64-amf-headers
#   3) bash tools/build-ffmpeg.sh
#
# Variables: SRC (fuente FFmpeg), OUT (carpeta de salida), FFMPEG_VERSION (tag).
# La integración con build-msi.ps1 usa tools/ffmpeg-build/ffmpeg.exe si existe;
# en caso contrario, descarga el "essentials" oficial como respaldo.
# ---------------------------------------------------------------------------
set -euo pipefail

SRC="${SRC:-$HOME/ffmpeg-src}"
OUT="${OUT:-/d/dev/agente_rec/tools/ffmpeg-build}"
FFMPEG_VERSION="${FFMPEG_VERSION:-n7.1}"

if [ ! -d "$SRC" ]; then
  echo "Clonando FFmpeg $FFMPEG_VERSION en $SRC..."
  git clone --depth 1 --branch "$FFMPEG_VERSION" https://github.com/FFmpeg/FFmpeg "$SRC"
fi

cd "$SRC"

echo "Configurando FFmpeg mínimo..."
./configure \
  --arch=x86_64 --target-os=mingw32 \
  --disable-doc --disable-programs --disable-network \
  --disable-autodetect --disable-everything \
  --enable-ffmpeg \
  --enable-gpl --enable-libx264 \
  --enable-d3d11va \
  --enable-libvpl \
  --enable-nvenc --enable-cuda-nvcc \
  --enable-amf \
  --enable-avdevice \
  --enable-protocol=file,pipe \
  --enable-indev=lavfi,gdigrab,dshow,wasapi \
  --enable-demuxer=rawvideo,matroska,mov,lavfi \
  --enable-muxer=matroska,mov,null \
  --enable-decoder=h264,aac,rawvideo \
  --enable-encoder=h264_qsv,h264_nvenc,h264_amf,libx264,aac \
  --enable-parser=h264,aac \
  --enable-filter=ddagrab,scale,scale_qsv,vpp_qsv,hwdownload,hwupload,format,aresample,amix,anullsrc,null,setpts,trim \
  --enable-bsf=h264_mp4toannexb,aac_adtstoasc \
  --pkg-config-flags=--static \
  --extra-ldflags="-static -static-libgcc"

echo "Compilando..."
make -j"$(nproc)"

mkdir -p "$OUT"
cp ffmpeg.exe "$OUT/ffmpeg.exe"
echo "OK: $OUT/ffmpeg.exe ($(du -h "$OUT/ffmpeg.exe" | cut -f1))"
