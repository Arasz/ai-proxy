async (page) => {
  const html = await (await page.request.get('http://127.0.0.1:18746/bundle.html')).text();
  const errors = [];
  page.on('pageerror', e => errors.push(e.message));
  await page.context().setOffline(true);
  try {
    await page.setContent(html, { waitUntil: 'load' });
    for (const provider of ['Anthropic', 'OpenAI', 'Default']) {
      await page.getByRole('tab', { name: provider, exact: true }).click();
      await page.getByRole('tabpanel', { name: provider, exact: true }).waitFor();
    }
    for (const chart of ['Data flow', 'Reduction logic']) {
      await page.getByRole('tab', { name: chart, exact: true }).click();
      await page.getByRole('tabpanel', { name: chart, exact: true }).waitFor();
    }
    await page.getByRole('button', { name: 'Plans 11', exact: true }).click();
    if (await page.locator('details.plan').count() !== 11) throw new Error('missing plan');
    await page.locator('details.plan').last().locator('summary').first().click();
    if (!await page.locator('details.plan[open]').count()) throw new Error('expand');
    await page.getByRole('textbox', { name: 'Search plans' }).fill('preview components');
    if (await page.locator('details.plan').count() !== 1) throw new Error('plan search');
    await page.getByRole('button', { name: 'Terms & evidence', exact: true }).click();
    await page.getByRole('textbox', { name: 'Search glossary' }).fill('CCR');
    if (!await page.locator('.term').count()) throw new Error('CCR search');
    await page.getByRole('textbox', { name: 'Search glossary' }).fill('none-xyz-000');
    if (!await page.locator('.empty').count()) throw new Error('empty state');
    await page.getByRole('textbox', { name: 'Search glossary' }).fill('');
    await page.getByRole('combobox', { name: 'Term category' }).selectOption('Transport');
    if (await page.locator('.term').count() !== 6) throw new Error('category');
    const widths = [320, 390, 1440, 1600, 1920, 2048];
    for (const section of ['Plans 11', 'Terms & evidence', 'Architecture']) {
      await page.getByRole('button', { name: section, exact: true }).click();
      for (const width of widths) {
        await page.setViewportSize({ width, height: 900 });
        if (!await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)) throw new Error(`overflow ${section} ${width}`);
      }
    }
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.getByRole('tab', { name: 'Anthropic', exact: true }).click();
    await page.getByRole('tab', { name: 'Anthropic', exact: true }).press('ArrowRight');
    if (await page.getByRole('tab', { name: 'OpenAI', exact: true }).getAttribute('aria-selected') !== 'true') throw new Error('keyboard');
    await page.getByRole('button', { name: 'Dark view', exact: true }).click();
    await page.getByRole('button', { name: 'Light view', exact: true }).click();
    await page.getByRole('tab', { name: 'Anthropic', exact: true }).click();
    await page.evaluate(() => scrollTo(0, 0));
    await page.screenshot({ path: '/tmp/ai-proxy-guide/final-desktop.png' });
    await page.setViewportSize({ width: 390, height: 844 });
    await page.evaluate(() => scrollTo(0, 0));
    await page.screenshot({ path: '/tmp/ai-proxy-guide/final-mobile.png' });
    if (errors.length) throw new Error(errors.join('\n'));
    return { offline: 'pass', providerTabs: 3, namedDiagramPanels: 2, plans: 11, search: 'pass', filters: 'pass', keyboard: 'pass', theme: 'pass', widths, horizontalOverflow: false, pageErrors: errors };
  } finally { await page.context().setOffline(false); }
}
