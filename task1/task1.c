/*
* task1.c
*/

#include<linux/module.h>
#include<linux/init.h>
#include<linux/kernel.h>

static int __init  helloinit(void)
{
	printk(KERN_INFO "Hello World\n");
	return 0;
}

static void __exit helloexit(void)
{
	printk(KERN_INFO "Bye from hello world module\n");
}


module_init(helloinit);
module_exit(helloexit);

MODULE_AUTHOR("Merwin");
MODULE_DESCRIPTION("Just a module");
MODULE_LICENSE("GPL");
