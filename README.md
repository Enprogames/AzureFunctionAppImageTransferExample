# Azure Function App Image Transfer Example

- Goal: Develop and deploy an azure function locally which can upload an image to Azure, download it again using a separate function, and verify that the image is the same.
    - Must use Modern, high-quality .NET 10 and C# 14, which native AOT compilation
    - Must use high-quality infrastructure as code, deployable from the local environment
    - An end-to-end test, in a test project, should be able to run a flow either locally through docker or through the cloud-deployed endpoints, and run the ‘upload an image, download the image again, then verify that the image has the same hash’ flow.

- Questions:
  - What is the best way to connect to the cloud setup with the infrastructure as code? and to deploy the infrastructure?
    - How can we ensure that we locally only have the necessary permissions?
    - How can we teardown all infrastructure?
