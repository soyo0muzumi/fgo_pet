// Production Chat assets with a synthetic bridge. Never a real native approval or model request.
// node tests/chat_approval_probe.cjs <playwright-module> <browser-executable> <ignored-output-directory>
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const assert = require('node:assert/strict');
const { chromium } = require(process.argv[2]);
const root = path.resolve(__dirname, '../plugins/FgoPet.Plugin.Dialogue/Desktop/ui/chat');
const output = path.resolve(process.argv[4]);
fs.mkdirSync(output, { recursive:true });
const record = (stage, result) => {
  const value = {time:new Date().toISOString(),request:'synthetic-approval',stage,result};
  fs.appendFileSync(path.join(output,'events.jsonl'),JSON.stringify(value)+'\n'); console.log(JSON.stringify(value));
};
const server = http.createServer((request,response) => {
  const relative = new URL(request.url,'http://localhost').pathname.slice(1) || 'index.html';
  const base = ['margin.css','margin-select.js'].includes(relative) ? path.resolve(root,'../../../../../src/FgoPet.UiSdk/Resources/Web') : root;
  const file = path.resolve(base,relative);
  if (!file.startsWith(base+path.sep) || !fs.existsSync(file) || !fs.statSync(file).isFile()) { response.writeHead(404).end(); return; }
  response.writeHead(200,{'Content-Type':{'.html':'text/html','.js':'text/javascript','.css':'text/css'}[path.extname(file)] || 'application/octet-stream'});
  response.end(fs.readFileSync(file));
});
(async()=>{
  let browser;
  await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
  try {
    record('session.start','started');
    browser = await chromium.launch({executablePath:process.argv[3],headless:true,args:['--no-sandbox']});
    const page = await browser.newPage({viewport:{width:376,height:600}});
    const errors=[]; page.on('pageerror',e=>errors.push(e.message));
    await page.addInitScript(()=>{
      const state=window.fixture={version:1,identity:{sessionId:'session',servantId:'role',conversationId:'conversation'},draft:{text:'Keep this draft',revision:1},
        presentation:{roleName:'Synthetic'},conversation:{turns:[],canSend:false,canStop:true,hasPendingRun:true},history:{items:[]}};
      window.commands=[]; let receive;
      window.pushFixture=()=>{state.version++;receive({data:{type:'chat.snapshot',payload:structuredClone(state)}});};
      window.setApproval=(requestId='request',expiry=Date.now()+60000)=>{
        state.conversation.pendingApproval={runId:'run',requestId,revision:5,expiresAt:new Date(expiry).toISOString(),toolName:'reminder.cancel',
          plainTextDetails:'<img src=x onerror=alert(1)>\nSynthetic full reminder\nUTC: 2027-01-02T00:00:00Z\nAsia/Shanghai\nID: synthetic-id\nVersion: 17\n'+ 'bounded details '.repeat(120),isSubmitting:false,errorText:''};
        Object.assign(state.conversation,{hasPendingRun:true,canStop:true,canSend:false});
      };
      window.setApproval(); window.chrome??={};
      window.chrome.webview={addEventListener(type,callback){receive=callback;},postMessage(message){
        window.commands.push(structuredClone(message));
        const approval=message.type.startsWith('chat.approval.');
        if(approval) Object.assign(state.conversation,{pendingApproval:null,hasPendingRun:false,canSend:true,canStop:false});
        if(message.type==='chat.draft')state.draft={text:message.payload.text,revision:message.payload.revision};
        state.version++;
        setTimeout(()=>receive({data:{type:'command.result',requestId:message.requestId,success:true,payload:{snapshot:structuredClone(state)}}}),approval?250:0);
      }};
    });
    await page.goto(`http://127.0.0.1:${server.address().port}/index.html`);
    await page.locator('#approval-card').waitFor();
    assert.equal(await page.locator('#approval-card img').count(),0);
    assert.match(await page.locator('.approval-details').textContent(),/Version: 17/);
    assert.match(await page.locator('.approval-details').textContent(),/Asia\/Shanghai/);
    assert.equal(await page.locator('#composer').inputValue(),'Keep this draft');
    assert.equal(await page.locator('#send-message').isDisabled(),true);
    await page.setViewportSize({width:328,height:450});
    assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth),false);
    await page.screenshot({path:path.join(output,'approval-full-details.png'),fullPage:true});
    await page.locator('#approval-review').click();
    await page.evaluate(()=>document.querySelector('#approval-review')?.click());
    assert.equal(await page.evaluate(()=>commands.filter(x=>x.type==='chat.approval.review').length),1);
    const review=await page.evaluate(()=>commands.find(x=>x.type==='chat.approval.review'));
    assert.deepEqual(Object.keys(review.payload).sort(),['sessionId','servantId','conversationId','runId','requestId','revision'].sort());
    assert.equal(review.payload.runId,'run'); assert.equal(review.payload.revision,5);
    await page.waitForFunction(()=>!document.getElementById('approval-card'));
    assert.equal(await page.locator('#composer').inputValue(),'Keep this draft');
    for (const action of ['deny','cancel']) {
      await page.evaluate(action=>{setApproval(action);pushFixture();},action);
      await page.locator(`#approval-${action}`).click();
      await page.waitForFunction(()=>!document.getElementById('approval-card'));
      assert.equal(await page.evaluate(action=>commands.filter(x=>x.type===`chat.approval.${action}`).length,action),1);
    }
    await page.evaluate(()=>{setApproval('expired',Date.now()-1000);pushFixture();});
    await page.locator('#approval-review').waitFor();
    assert.equal(await page.locator('#approval-review').isDisabled(),true);
    assert.equal(await page.locator('#approval-deny').isDisabled(),true);
    await page.evaluate(()=>{fixture.identity.conversationId='another';fixture.conversation.pendingApproval=null;pushFixture();});
    await page.waitForFunction(()=>!document.getElementById('approval-card'));
    assert.equal(await page.evaluate(()=>commands.some(x=>x.type==='chat.approval.allow')),false);
    assert.deepEqual(errors,[]);
    record('literal-details-review-only-replay-deny-cancel-expiry-scope-draft','passed');
    record('session.complete','passed');
  } finally { await browser?.close(); server.close(); }
})().catch(error=>{record('session.failed',error.name);console.error(error);process.exitCode=1;});
