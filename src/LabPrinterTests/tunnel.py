"""Forward local TCP connections to a host behind the jump host, one `plink -nc` per connection.

A plink listener runs on Windows, and Windows may drop connections from WSL to it, so the listener runs in WSL
and plink only makes outbound connections.

Usage: python3 -I tunnel.py <plink> <jump host> <target host:port> <local port>
"""

import asyncio
import sys

PLINK, JUMP_HOST, TARGET, LOCAL_PORT = sys.argv[1], sys.argv[2], sys.argv[3], int(sys.argv[4])


async def handle(client_reader, client_writer):
    plink = await asyncio.create_subprocess_exec(
        PLINK, '-ssh', '-agent', '-batch', '-nc', TARGET, JUMP_HOST,
        stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.DEVNULL)

    async def upstream():
        try:
            while data := await client_reader.read(65536):
                plink.stdin.write(data)
                await plink.stdin.drain()
        finally:
            plink.stdin.close()

    async def downstream():
        try:
            while data := await plink.stdout.read(65536):
                client_writer.write(data)
                await client_writer.drain()
        finally:
            client_writer.close()

    await asyncio.gather(upstream(), downstream(), return_exceptions=True)
    if plink.returncode is None:
        plink.kill()
    await plink.wait()


async def main():
    server = await asyncio.start_server(handle, '127.0.0.1', LOCAL_PORT)
    async with server:
        await server.serve_forever()


asyncio.run(main())
