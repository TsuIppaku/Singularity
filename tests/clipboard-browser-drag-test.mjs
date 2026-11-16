// Isolated browser acceptance fixture. It injects CDP DragData (not Windows
// OLE), never opens the system clipboard, moves a real mouse, or uploads data.
// Requires Node 22+ (built-in WebSocket) and an installed Chromium browser.
// First run: artifacts\ClipboardDragInteropTests.exe --keep-fixtures
// Then: node tests\clipboard-browser-drag-test.mjs <FIXTURE_ROOT> [browser.exe]
// JSON evidence is saved as <FIXTURE_ROOT>\browser-results.json. The browser
// and this script's unique profile are closed/removed after the run. The input
// export fixture remains available for review and can be removed separately.
import fs from 'node:fs/promises';
import path from 'node:path';
import {spawn} from 'node:child_process';
import {randomUUID} from 'node:crypto';
const fixtureRoot = process.argv[2];
const browserPath = process.argv[3] || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';
if (!fixtureRoot || !path.basename(fixtureRoot).startsWith('OrbitDragInterop-')) throw new Error('An isolated native-test fixture root is required.');
const textPath = await fs.readFile(path.join(fixtureRoot, 'text-path.txt'), 'utf8');
const imagePath = await fs.readFile(path.join(fixtureRoot, 'image-path.txt'), 'utf8');
const text = await fs.readFile(path.join(fixtureRoot, 'expected-text.txt'), 'utf8');
const profile = path.resolve(fixtureRoot, 'browser-profile-' + randomUUID());
if(path.dirname(profile)!==path.resolve(fixtureRoot))throw new Error('Profile is outside isolated fixture root.');
const browser = spawn(browserPath, [
  '--headless=new', '--remote-debugging-port=0', '--no-first-run', '--no-default-browser-check',
  '--disable-background-networking', '--disable-component-update', '--disable-sync', '--disable-extensions',
  '--disable-default-apps', '--metrics-recording-only', `--user-data-dir=${profile}`, 'about:blank'
], {windowsHide:true, stdio:['ignore','ignore','pipe']});
let socket, nextId = 0;
const pending = new Map();
let fatalTimer;
const results = {scope:'Chrome renderer receives CDP DragData, with real files in an isolated temporary directory. No OS OLE drag, clipboard access, external website or upload.', checks:[]};
const wait = ms => new Promise(resolve => setTimeout(resolve, ms));
function check(ok, name, evidence) {
  results.checks.push({ok:!!ok,name,evidence});
  console.log(`${ok ? 'PASS' : 'FAIL'}: ${name}`);
  if (!ok) throw new Error(name + ': ' + JSON.stringify(evidence));
}
function rpc(method, params={}, sessionId) {
  return new Promise((resolve,reject) => {
    const id = ++nextId;
    const timer = setTimeout(() => {pending.delete(id);reject(new Error('CDP timeout: '+method));},15000);
    pending.set(id,{resolve,reject,timer});
    socket.send(JSON.stringify({id,method,params,...(sessionId?{sessionId}:{})}));
  });
}
try {
  fatalTimer = setTimeout(() => {console.error('Browser fixture timed out');browser.kill();process.exitCode=1;},55000);
  const endpoint = await new Promise((resolve,reject) => {
    let errors='';
    const timer=setTimeout(()=>reject(new Error('No DevTools endpoint: '+errors)),15000);
    browser.stderr.on('data',chunk=>{errors+=chunk;const match=errors.match(/DevTools listening on (ws:\/\/[^\s]+)/);if(match){clearTimeout(timer);resolve(match[1]);}});
    browser.once('error',reject);
    browser.once('exit',code=>reject(new Error('Browser exited before endpoint: '+code+' '+errors)));
  });
  socket = new WebSocket(endpoint);
  await new Promise((resolve,reject)=>{socket.addEventListener('open',resolve,{once:true});socket.addEventListener('error',reject,{once:true});});
  socket.addEventListener('message',event=>{
    const message=JSON.parse(event.data);
    const request=pending.get(message.id);
    if(!request)return;
    pending.delete(message.id);clearTimeout(request.timer);
    if(message.error)request.reject(new Error(JSON.stringify(message.error)));else request.resolve(message.result);
  });
  results.browser=await rpc('Browser.getVersion');
  const {targetId}=await rpc('Target.createTarget',{url:'about:blank'});
  const {sessionId}=await rpc('Target.attachToTarget',{targetId,flatten:true});
  const run=(method,params={})=>rpc(method,params,sessionId);
  await run('Page.enable');
  await run('Runtime.enable');
  await run('Emulation.setDeviceMetricsOverride',{width:900,height:650,deviceScaleFactor:1,mobile:false});
  const {frameTree}=await run('Page.getFrameTree');
  await run('Page.setDocumentContent',{frameId:frameTree.frame.id,html:`<!doctype html><html><head><meta charset="utf-8"><style>body{font:16px Arial;margin:24px}textarea,input,[contenteditable],#custom{display:block;width:600px;margin:12px 0;padding:10px;box-sizing:border-box}textarea{height:84px}[contenteditable],#custom{height:70px;border:1px solid gray}input{height:40px}</style></head><body><textarea id="textarea"></textarea><input id="textinput" type="text"><div id="editable" contenteditable="true"></div><input id="upload" type="file"><div id="custom">custom attachment target</div><script>
    window.dropLogs=[];
    for(const id of ['textarea','textinput','editable','upload','custom']){
      const node=document.getElementById(id);
      node.addEventListener('drop',event=>{
        const data=event.dataTransfer;
        window.dropLogs.push({id,types:[...data.types],text:data.getData('text/plain'),files:[...data.files].map(file=>({name:file.name,type:file.type,size:file.size})),items:[...data.items].map(item=>({kind:item.kind,type:item.type}))});
        if(id==='custom'){event.preventDefault();window.customPreference=data.files.length?'file':'text';}
      });
      if(id==='custom')node.addEventListener('dragover',event=>event.preventDefault());
    }
  <\/script></body></html>`});
  async function evaluate(expression){const result=await run('Runtime.evaluate',{expression,returnByValue:true,awaitPromise:true});if(result.exceptionDetails)throw new Error(JSON.stringify(result.exceptionDetails));return result.result.value;}
  async function drop(id,files,withText){
    const point=await evaluate(`(()=>{const r=document.getElementById(${JSON.stringify(id)}).getBoundingClientRect();return{x:r.left+30,y:r.top+r.height/2}})()`);
    const data={items:withText?[{mimeType:'text/plain',data:text}]:[],files,dragOperationsMask:1};
    await run('Input.dispatchDragEvent',{type:'dragEnter',...point,data});
    await run('Input.dispatchDragEvent',{type:'dragOver',...point,data});
    await run('Input.dispatchDragEvent',{type:'drop',...point,data});
    await wait(80);
  }
  await drop('textarea',[textPath],true);
  let value=await evaluate('document.getElementById("textarea").value');
  check(value===text.replace(/\r\n/g,'\n'),'Mixed file + text inserts text into a plain textarea',value);
  await drop('textinput',[textPath],true);
  value=await evaluate('document.getElementById("textinput").value');
  check(value===text.replace(/\r?\n/g,' '),'Mixed file + text inserts text into a single-line input',value);
  await drop('editable',[textPath],true);
  value=await evaluate('document.getElementById("editable").innerText');
  check(value.replace(/\r\n/g,'\n')===text.replace(/\r\n/g,'\n'),'Mixed file + text inserts text into contenteditable',value);
  await drop('upload',[textPath],true);
  value=await evaluate('(async()=>{const f=document.getElementById("upload").files[0];return f?{name:f.name,type:f.type,text:await f.text()}:null})()');
  check(value?.name===path.basename(textPath)&&value.text===text,'Mixed file + text populates file upload input with real TXT bytes',value);
  await evaluate('document.getElementById("upload").value=""');
  await drop('upload',[imagePath],false);
  value=await evaluate('(async()=>{const f=document.getElementById("upload").files[0];return f?{name:f.name,type:f.type,size:f.size,signature:[...new Uint8Array(await f.slice(0,8).arrayBuffer())]}:null})()');
  check(value?.name===path.basename(imagePath)&&value.type==='image/png'&&value.signature.join(',')==='137,80,78,71,13,10,26,10','Image FileDrop populates upload input with real PNG bytes',value);
  await drop('custom',[textPath],true);
  value=await evaluate('({preference:window.customPreference,log:window.dropLogs[window.dropLogs.length-1]})');
  check(value.preference==='file'&&value.log.text===text&&value.log.files.length===1&&value.log.types.includes('Files')&&value.log.types.includes('text/plain'),'Custom website handler can prioritize file while both formats remain available',value);
  results.logs=await evaluate('window.dropLogs');
  results.passed=true;
  console.log(`PASS: ${results.checks.length} isolated headless-browser acceptance checks (${results.browser.product}).`);
} catch(error) {
  results.passed=false;results.error=String(error.stack||error);console.error(results.error);process.exitCode=1;
} finally {
  clearTimeout(fatalTimer);
  await fs.writeFile(path.join(fixtureRoot,'browser-results.json'),JSON.stringify(results,null,2));
  if(socket?.readyState===WebSocket.OPEN){try{await rpc('Browser.close');}catch{}socket.close();}
  if(browser.exitCode===null)browser.kill();
  for(const request of pending.values())clearTimeout(request.timer);
  // Remove only this invocation's random profile after the owned browser has
  // exited. A locked directory is reported and left intact; never broaden the
  // deletion path or fall back to a user browser profile.
  for(let attempt=0;attempt<20&&browser.exitCode===null;attempt++)await wait(50);
  try { await fs.rm(profile,{recursive:true,force:true,maxRetries:5,retryDelay:100}); }
  catch(error){console.error('Temporary profile retained: '+profile+' ('+error.message+')');}
}
