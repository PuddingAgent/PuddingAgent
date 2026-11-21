import { randomInt } from 'node:crypto';
import { once } from 'node:events';

export async function listenLoopback(server) {
  for (let attempt = 0; attempt < 12; attempt++) {
    // Windows may allocate ports such as 6665 with listen(0), which Fetch refuses.
    const port = randomInt(49152, 65536);
    server.listen(port, '127.0.0.1');
    try {
      await once(server, 'listening');
      return port;
    } catch (error) {
      if (!['EACCES', 'EADDRINUSE'].includes(error.code) || attempt === 11) throw error;
    }
  }
}
