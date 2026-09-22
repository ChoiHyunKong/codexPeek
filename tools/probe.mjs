import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';
const p=spawn('codex',['app-server'],{windowsHide:true,stdio:['pipe','pipe','pipe']});
const timer=setTimeout(()=>{console.error('Timed out');p.kill();process.exitCode=1;},30000);
const send=(id,method,params)=>p.stdin.write(JSON.stringify({id,method,...(params===undefined?{}:{params})})+'\n');
p.stderr.on('data',()=>{});
p.on('error',e=>{console.error(e.message);clearTimeout(timer);process.exitCode=1;});
createInterface({input:p.stdout}).on('line',line=>{
 let m;try{m=JSON.parse(line)}catch{return}
 if(m.id===1){if(m.error){console.error(m.error);p.kill();clearTimeout(timer);return}p.stdin.write('{"method":"initialized"}\n');send(2,'account/read',{refreshToken:false});}
 if(m.id===2){console.log(JSON.stringify({accountType:m.result?.account?.type,plan:m.result?.account?.planType,error:m.error}));send(3,'account/rateLimits/read');}
 if(m.id===3){console.log(JSON.stringify(m));clearTimeout(timer);p.kill();if(m.error)process.exitCode=1;}
});
send(1,'initialize',{clientInfo:{name:'codex_peek_probe',version:'0.1.0'}});
