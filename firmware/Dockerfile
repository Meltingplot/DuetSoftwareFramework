# Build environment for the firmware in this folder. It follows the release workflows of the Meltingplot forks
# (RepRapFirmware and Duet3Expansion, .github/workflows/mp-release.yml): Arm GNU Toolchain 15.2.Rel1 and Duet3D's CrcAppender,
# then the top-level Makefile of each project. g++ builds the host harness of the SBC link (RepRapFirmware/Tools/sbc-harness).
# Used by build.sh, see README.md
FROM ubuntu:24.04

ARG ARM_TOOLCHAIN=arm-gnu-toolchain-15.2.rel1-x86_64-arm-none-eabi
# From developer.arm.com, $ARM_TOOLCHAIN.tar.xz.sha256asc
ARG ARM_TOOLCHAIN_SHA256=597893282ac8c6ab1a4073977f2362990184599643b4c5ee34870a8215783a16
ARG CRC_APPENDER_VERSION=1.4.0
ARG CRC_APPENDER_SHA256=b86e7b96d3373d3012029c70c1c3af1cb4be7f806b281bce335310b415148fb9

RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl xz-utils make g++ python3 \
    && rm -rf /var/lib/apt/lists/*

RUN curl -fsSL -o /tmp/toolchain.tar.xz "https://developer.arm.com/-/media/Files/downloads/gnu/15.2.rel1/binrel/${ARM_TOOLCHAIN}.tar.xz" \
    && echo "${ARM_TOOLCHAIN_SHA256}  /tmp/toolchain.tar.xz" | sha256sum -c - \
    && mkdir -p /opt/arm-gnu-toolchain \
    && tar -xf /tmp/toolchain.tar.xz -C /opt/arm-gnu-toolchain --strip-components=1 \
    && rm /tmp/toolchain.tar.xz

RUN curl -fsSL -o /usr/local/bin/CrcAppender "https://github.com/Duet3D/CrcAppender/releases/download/v${CRC_APPENDER_VERSION}/CrcAppender-linux-x64" \
    && echo "${CRC_APPENDER_SHA256}  /usr/local/bin/CrcAppender" | sha256sum -c - \
    && chmod +x /usr/local/bin/CrcAppender

ENV CROSS_COMPILE=/opt/arm-gnu-toolchain/bin/arm-none-eabi-
WORKDIR /work
