import { chromium } from '@playwright/test';
import { spawn } from 'node:child_process';
import { mkdtemp, readFile, writeFile, mkdir } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import assert from 'node:assert/strict';

// The production binary runs against an isolated fixture; no test command is compiled into it.
const fixture = await mkdtemp(join(tmpdir(), 'chatgpt-api-only-example-'));
const port = 19227;
const executable = resolve('target/release/ChatGPTApiOnly.exe');
const child = spawn(executable, [], { windowsHide: true, env: {
  ...process.env, CHATGPT_API_ONLY_CONFIG_DIR: fixture,
  WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: `--remote-debugging-port=${port}`,
} });
let browser;
try {
  for (let attempt = 0; attempt < 120; attempt++) {
    if (child.exitCode !== null) throw new Error(`Desktop exited with ${child.exitCode}`);
    try { const response = await fetch(`http://127.0.0.1:${port}/json/version`); if (response.ok) break; } catch {}
    await new Promise(resolve => setTimeout(resolve, 250));
  }
  browser = await chromium.connectOverCDP(`http://127.0.0.1:${port}`);
  const context = browser.contexts()[0];
  const page = context.pages()[0] ?? await context.waitForEvent('page');
  await page.getByRole('tab', { name: '自定义 API', exact: true }).click();
  await page.getByRole('button', { name: '添加', exact: true }).click();
  for (const [label, value] of Object.entries({ '配置名称': 'example API', '提供者显示名称': 'example provider', 'API 地址': 'https://api.example.com/v1', 'API Key': 'example-key', '模型': 'example-model' })) {
    await page.getByLabel(label, {exact: true}).fill(value);
  }
  await page.getByRole('button', {name:'启动',exact:true}).click();
  await page.getByRole('alert').filter({hasText:'未保存修改'}).waitFor();
  await page.getByRole('button', {name:'保存配置',exact:true}).click();
  await page.getByRole('status').filter({hasText:'已保存'}).waitFor();
  assert.match(await readFile(join(fixture,'config.toml'),'utf8'), /model_provider = "custom"/);
  assert.equal(JSON.parse(await readFile(join(fixture,'auth.json'),'utf8')).OPENAI_API_KEY,'example-key');
  await mkdir('.impeccable/review',{recursive:true});
  await page.screenshot({path:'.impeccable/review/native-windows.png'});
  await writeFile(join(fixture,'.env'),'EXAMPLE=external\n');
  await page.getByLabel('配置名称').fill('example retained draft');
  await page.getByRole('button', {name:'保存配置',exact:true}).click();
  await page.getByRole('alert').filter({hasText:'其他程序修改'}).waitFor();
  assert.equal(await page.getByLabel('配置名称').inputValue(),'example retained draft');
  await page.getByRole('button', {name:'关闭',exact:true}).click();
  await page.getByRole('button', {name:'继续编辑',exact:true}).click();
  await page.getByRole('alertdialog').waitFor({state:'hidden'});
  await page.keyboard.press('Escape');
  await page.getByRole('button', {name:'放弃修改并关闭',exact:true}).click();
  console.log('PASS: production Tauri window, actual IPC, isolated save, dirty/external conflict, close cancellation and Escape');
  console.log(`Fixture: ${fixture}`);
} finally {
  await browser?.close();
  if (child.exitCode === null) child.kill();
}
