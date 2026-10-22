import { createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

const env = await builder.addAzureContainerAppEnvironment("env");

const app = await builder
    .addNodeApp("app", "./api", "src/index.ts")
    .withExternalHttpEndpoints();

await builder.build().run();
