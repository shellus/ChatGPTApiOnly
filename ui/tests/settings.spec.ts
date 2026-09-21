import { test, expect, Page } from "@playwright/test";
async function fixture(page: Page) {
  await page.addInitScript(() => {
    const callbacks = new Map<number, (v: unknown) => void>();
    let n = 0;
    const listeners = new Map<string, number>();
    const draft = {
      mode: "official",
      library: {
        official_accounts: [
          {
            id: "example-account",
            name: "example 官方账号",
            official_auth: null,
          },
        ],
        custom_providers: [{ id: "example-api", name: "example API" }],
        selected_official: "example-account",
        selected_custom: "example-api",
        official_proxy_url: "",
        official_model: "",
        official_effort: "",
      },
      custom_fields: {
        "example-api": {
          provider_name: "example",
          base_url: "https://api.example.com/v1",
          api_key: "example-key",
          model: "example-model",
          effort: "medium",
        },
      },
    };
    let view = {
      revision: "example-revision",
      draft,
      config_dir: "example-fixture",
    };
    const win = window as unknown as Record<string, any>;
    win.calls = [];
    win.__TAURI_INTERNALS__ = {
      transformCallback: (cb: (v: unknown) => void) => {
        callbacks.set(++n, cb);
        return n;
      },
      unregisterCallback: (id: number) => callbacks.delete(id),
      invoke: async (command: string, args: any) => {
        win.calls.push({ command, args });
        if (command === "plugin:event|listen") {
          listeners.set(args.event, args.handler);
          return ++n;
        }
        if (command === "plugin:event|unlisten") return;
        if (command === "load") return structuredClone(view);
        if (command === "save") {
          if (win.failSave) throw "example 保存失败";
          view = { ...view, draft: args.draft };
          return structuredClone(view);
        }
        if (command === "launch" && win.failLaunch)
          throw "example 客户端未安装";
        if (command === "repair") {
          callbacks.get(listeners.get("repair-progress")!)?.({
            event: "repair-progress",
            payload: { phase: "备份对话", completed: 1, total: 2 },
          });
          return { changed: 2, backup: "example-backup" };
        }
      },
    };
    win.__TAURI_EVENT_PLUGIN_INTERNALS__ = { unregisterListener: () => {} };
    win.requestNativeClose = () =>
      callbacks.get(listeners.get("request-close")!)?.({
        event: "request-close",
        payload: null,
      });
  });
  await page.goto("/");
  await expect(page.getByLabel("配置名称")).toHaveValue("example 官方账号");
}
const calls = (page: Page, command: string) =>
  page.evaluate(
    (c) => (window as any).calls.filter((x: any) => x.command === c).length,
    command,
  );

test("opens settings; save and keyboard never start client; dirty launch blocked", async ({
  page,
}) => {
  await fixture(page);
  await page.getByLabel("配置名称").fill("example edited");
  await page.keyboard.press("Enter");
  await page.keyboard.press("Space");
  expect(await calls(page, "launch")).toBe(0);
  expect(await calls(page, "save")).toBe(0);
  await page.getByRole("button", { name: "启动", exact: true }).click();
  await expect(page.getByRole("alert")).toContainText("未保存修改");
  expect(await calls(page, "launch")).toBe(0);
  await page.getByRole("button", { name: "保存配置", exact: true }).click();
  await expect(page.getByRole("status")).toContainText("已保存");
  expect(await calls(page, "launch")).toBe(0);
  expect(await calls(page, "close")).toBe(0);
  await page.getByRole("button", { name: "启动", exact: true }).click();
  expect(await calls(page, "launch")).toBe(1);
});
test("close button, Escape and native titlebar share discard confirmation", async ({
  page,
}) => {
  await fixture(page);
  await page.getByLabel("配置名称").fill("example changed");
  for (const route of ["button", "escape", "native"]) {
    if (route === "button")
      await page.getByRole("button", { name: "关闭", exact: true }).click();
    if (route === "escape") await page.keyboard.press("Escape");
    if (route === "native")
      await page.evaluate(() => (window as any).requestNativeClose());
    await expect(page.getByRole("alertdialog")).toBeVisible();
    await page.getByRole("button", { name: "继续编辑" }).click();
    await expect(page.getByRole("alertdialog")).toBeHidden();
    expect(await calls(page, "close")).toBe(0);
  }
  await page.getByRole("button", { name: "关闭", exact: true }).click();
  await page.getByRole("button", { name: "放弃修改并关闭" }).click();
  expect(await calls(page, "close")).toBe(1);
});
test("failed save retains draft and failed launch stays open for retry", async ({
  page,
}) => {
  await fixture(page);
  await page.evaluate(() => ((window as any).failSave = true));
  await page.getByLabel("配置名称").fill("example retained");
  await page.getByRole("button", { name: "保存配置", exact: true }).click();
  await expect(page.getByRole("alert")).toContainText("保存失败");
  await expect(page.getByLabel("配置名称")).toHaveValue("example retained");
  await page.evaluate(() => {
    (window as any).failSave = false;
    (window as any).failLaunch = true;
  });
  await page.getByRole("button", { name: "保存配置", exact: true }).click();
  await page.getByRole("button", { name: "启动", exact: true }).click();
  await expect(page.getByRole("alert")).toContainText("客户端未安装");
  expect(await calls(page, "close")).toBe(0);
  await page.evaluate(() => ((window as any).failLaunch = false));
  await page.getByRole("button", { name: "启动", exact: true }).click();
  expect(await calls(page, "launch")).toBe(2);
});
test("mode and profile edits stay drafts; history repair is explicit", async ({
  page,
}) => {
  await fixture(page);
  await page.getByRole("tab", { name: "自定义 API", exact: true }).click();
  await page.getByRole("button", { name: "复制", exact: true }).click();
  await expect(page.getByLabel("配置名称")).toHaveValue("example API 副本");
  expect(await calls(page, "save")).toBe(0);
  await page.getByRole("button", { name: "修复对话", exact: true }).click();
  await page.getByRole("button", { name: "继续编辑" }).click();
  expect(await calls(page, "repair")).toBe(0);
  await page.getByRole("button", { name: "修复对话", exact: true }).click();
  await page.getByRole("button", { name: "备份并修复" }).click();
  await expect(page.getByRole("status")).toContainText("已修复 2 项");
  expect(await calls(page, "save")).toBe(0);
});
test("settings fit desktop, compact window and large text in both themes", async ({
  page,
}) => {
  await fixture(page);
  for (const [width, height] of [
    [820, 720],
    [600, 580],
  ]) {
    await page.setViewportSize({ width, height });
    for (const mode of ["官方账号", "自定义 API"]) {
      await page.getByRole("tab", { name: mode, exact: true }).click();
      await expect(
        page.getByRole("button", { name: "保存配置", exact: true }),
      ).toBeVisible();
      expect(
        await page.evaluate(
          () => document.documentElement.scrollWidth <= innerWidth,
        ),
      ).toBe(true);
      await page.screenshot({
        path: `.impeccable/review/${width}-${mode}.png`,
        fullPage: true,
      });
    }
  }
  await page.getByRole("button", { name: "深色", exact: true }).click();
  await page.screenshot({
    path: ".impeccable/review/dark.png",
    fullPage: true,
  });
  await page.evaluate(() => (document.documentElement.style.fontSize = "24px"));
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth,
    ),
  ).toBe(true);
});
