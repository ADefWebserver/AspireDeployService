import { createBuilder } from './.aspire/modules/aspire.mjs';
const builder = await createBuilder();
const a = await builder.addAzureContainerAppEnvironment("a");
const b = await builder.addAzureContainerAppEnvironment('b')
    .withAzureContainerRegistry(x);
await builder.build().run();
