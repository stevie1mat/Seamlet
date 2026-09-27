#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/.."
mkdir -p build/swift-cache
dotnet run --project tests/ProtocolTests.csproj -- "$PWD/build/csharp-vector.txt"
swiftc -swift-version 5 -module-cache-path "$PWD/build/swift-cache" macos/Wire.swift tests/ProtocolTests.swift -o build/protocol-tests
build/protocol-tests build/csharp-vector.txt build/swift-vector.txt
dotnet run --project tests/ProtocolTests.csproj -- verify "$PWD/build/swift-vector.txt"
swiftc -swift-version 5 -module-cache-path "$PWD/build/swift-cache" macos/Discovery.swift tests/DiscoveryHost.swift -o build/discovery-test-host
dotnet run --project tests/ProtocolTests.csproj -- discovery "$PWD/build/discovery-test-host"
swiftc -swift-version 5 -module-cache-path "$PWD/build/swift-cache" macos/Wire.swift macos/FileTransfer.swift tests/FileHost.swift -o build/file-test-host
dotnet run --project tests/ProtocolTests.csproj -- files "$PWD/build/file-test-host"
