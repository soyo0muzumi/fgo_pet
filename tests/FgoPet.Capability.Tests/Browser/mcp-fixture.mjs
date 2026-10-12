// Synthetic protocol fixture: no browser, network, personal data or model calls.
import { createInterface } from 'node:readline';
let initialized = false;
let sequence = 0;
const input = createInterface({ input: process.stdin });
const reply = (id, result) => process.stdout.write(JSON.stringify({ jsonrpc: '2.0', id, result }) + '\n');
input.on('line', line => {
  const request = JSON.parse(line);
  if (request.method === 'initialize') {
    reply(request.id, { protocolVersion: '2025-06-18', capabilities: { tools: {} }, serverInfo: { name: 'fixture', version: '1.10.1' } });
  } else if (request.method === 'notifications/initialized') {
    initialized = true;
  } else if (request.method === 'tools/list' && initialized) {
    reply(request.id, { tools: ['list_pages', 'new_page', 'take_snapshot'].map(name => ({ name })) });
  } else if (request.method === 'tools/call' && initialized) {
    sequence++;
    const mode = request.params.arguments.fixtureMode;
    if (mode === 'exit') { process.exit(0); }
    if (mode === 'wait') { return; }
    if (mode === 'oversize') { process.stdout.write('x'.repeat(2097153) + '\n'); return; }
    if (mode === 'error') {
      process.stdout.write(JSON.stringify({ jsonrpc: '2.0', id: request.id, error: { code: -32603, message: 'secret should never escape' } }) + '\n');
      return;
    }
    const safeFlags = ['--autoConnect', '--experimental-structured-content', '--no-usage-statistics', '--no-performance-crux',
      '--javascript-evaluation=false', '--file-navigations=false'].every(flag => process.argv.includes(flag))
      && process.env.CHROME_DEVTOOLS_MCP_NO_CONFIG_DISCOVERY === '1';
    const profileIndex = process.argv.indexOf('--user-data-dir');
    const explicitProfile = profileIndex >= 0 && process.argv[profileIndex + 1] === request.params.arguments.expectedDirectory;
    reply(request.id, { sequence, safeFlags, explicitProfile });
  }
});
